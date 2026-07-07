using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.Dashboards;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Application.Shared.Services.Data;

public enum DashboardOpType { None, AddWidget, UpdateWidget, RemoveWidget, RenameWidget }

public class DashboardOperation
{
    public DashboardOpType Type { get; set; } = DashboardOpType.None;
    public string? WidgetId { get; set; }
    public string? Title { get; set; }
    public string? VizType { get; set; }
    public string? Sql { get; set; }
    public WidgetConfig? Config { get; set; }
}

public class DashboardWidgetContext
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string VizType { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
}

public class DashboardAgentRequest
{
    public string DatasetId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<DashboardWidgetContext> ExistingWidgets { get; set; } = new();
}

public class DashboardAgentResult
{
    public string Reply { get; set; } = string.Empty;
    public List<DashboardOperation> Operations { get; set; } = new();
}

public interface IDashboardAgentService
{
    /// <summary>
    /// Turns a natural-language message into validated dashboard operations. Each add/update op's SQL
    /// is checked (SELECT-only) and executed once against the dataset's read-only path to confirm it
    /// works; failures trigger one self-correction retry, then the op is dropped. Never throws.
    /// </summary>
    Task<DashboardAgentResult> PlanAsync(DashboardAgentRequest request, CancellationToken ct = default);
}

public class DashboardAgentService : IDashboardAgentService
{
    private const int MaxSchemaTables = 15;
    private const int SampleRows = 3;
    private const int ValidationRowCap = 50;

    // Small in-process cache of the schema prompt per dataset (short TTL — schema rarely changes mid-session).
    private static readonly ConcurrentDictionary<string, (DateTime CachedAt, string Schema)> SchemaCache = new();
    private static readonly TimeSpan SchemaTtl = TimeSpan.FromMinutes(10);

    private readonly AzureOpenAIConfiguration _config;
    private readonly AzureOpenAIClient _client;
    private readonly IDatasetService _datasetService;
    private readonly IDuckdbService _duckdbService;
    private readonly IDatabaseTableService _databaseTableService;
    private readonly ILogger<DashboardAgentService> _logger;

    public DashboardAgentService(
        IOptions<AzureOpenAIConfiguration> config,
        IDatasetService datasetService,
        IDuckdbService duckdbService,
        IDatabaseTableService databaseTableService,
        ILogger<DashboardAgentService> logger)
    {
        _config = config.Value;
        _client = new AzureOpenAIClient(new Uri(_config.Endpoint), new AzureKeyCredential(_config.ApiKey));
        _datasetService = datasetService;
        _duckdbService = duckdbService;
        _databaseTableService = databaseTableService;
        _logger = logger;
    }

    public async Task<DashboardAgentResult> PlanAsync(DashboardAgentRequest request, CancellationToken ct = default)
    {
        var result = new DashboardAgentResult();

        var dataset = await _datasetService.GetDatasetAsync(request.DatasetId, request.UserId);
        if (dataset == null)
        {
            result.Reply = "I couldn't find that dataset, or you don't have access to it.";
            return result;
        }

        string schema;
        try
        {
            schema = await GetSchemaContextAsync(dataset, request.CompanyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DashboardAgent] Failed to build schema context for dataset {DatasetId}.", request.DatasetId);
            result.Reply = "I couldn't read this dataset's schema, so I can't build a widget right now.";
            return result;
        }

        try
        {
            var chatClient = _client.GetChatClient(_config.DeploymentName);
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(BuildSystemPrompt(schema, request.ExistingWidgets)),
                new UserChatMessage(request.Message),
            };
            var options = new ChatCompletionOptions
            {
                Temperature = 0f,
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_config.TimeoutSeconds, 45)));

            var response = await chatClient.CompleteChatAsync(messages, options, cts.Token);
            var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                result.Reply = "The assistant didn't return anything. Please try rephrasing.";
                return result;
            }

            var parsed = Parse(content);
            result.Reply = parsed.Reply;

            foreach (var op in parsed.Operations)
            {
                if (op.Type is DashboardOpType.None or DashboardOpType.RemoveWidget or DashboardOpType.RenameWidget)
                {
                    result.Operations.Add(op);
                    continue;
                }

                // add/update: validate + preview the SQL, with one self-correction retry.
                var validated = await ValidateAndFixAsync(op, dataset, request.CompanyId, chatClient, messages, content, cts.Token);
                if (validated != null)
                    result.Operations.Add(validated);
                else
                    result.Reply += $"\n\n(I couldn't produce a working query for \"{op.Title}\", so I skipped it.)";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Reply = "The request timed out. Please try again.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DashboardAgent] Planning failed for dataset {DatasetId}.", request.DatasetId);
            result.Reply = "Something went wrong while building the dashboard. Please try again.";
            return result;
        }
    }

    // ---- validation / execution ----

    private async Task<DashboardOperation?> ValidateAndFixAsync(
        DashboardOperation op, Dataset dataset, string companyId,
        ChatClient chatClient, List<OpenAI.Chat.ChatMessage> messages, string firstResponse, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!SelectOnlyGuard.IsSafeSelect(op.Sql, out var guardError))
            {
                if (attempt == 1) return null;
                op = await AskForFixAsync(op, guardError!, chatClient, messages, firstResponse, ct) ?? op;
                continue;
            }

            var exec = await ExecuteAsync(dataset, companyId, op.Sql!, ct);
            if (exec.Error == null && exec.IsSelect)
                return op; // works

            if (attempt == 1) return null;
            var fixMsg = exec.Error ?? "The query did not return a result set.";
            op = await AskForFixAsync(op, fixMsg, chatClient, messages, firstResponse, ct) ?? op;
        }
        return null;
    }

    private async Task<SqlQueryResult> ExecuteAsync(Dataset dataset, string companyId, string sql, CancellationToken ct)
    {
        // Mirror QueryController.Run routing: External runs against the live read-only source; Local (and
        // anything else) runs against the dataset's DuckDB, read-only.
        if (dataset.SourceType == DatasetSourceType.External && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            return await _databaseTableService.ExecuteQueryAsync(dataset.SourceEntityId!, companyId, sql, ValidationRowCap, ct);

        return await _duckdbService.ExecuteSqlAsync(dataset.Id!, sql, allowWrite: false, maxRows: ValidationRowCap, ct);
    }

    private async Task<DashboardOperation?> AskForFixAsync(
        DashboardOperation op, string error, ChatClient chatClient,
        List<OpenAI.Chat.ChatMessage> messages, string firstResponse, CancellationToken ct)
    {
        var fixMessages = new List<OpenAI.Chat.ChatMessage>(messages)
        {
            new AssistantChatMessage(firstResponse),
            new UserChatMessage(
                $"The query for the widget titled \"{op.Title}\" failed with this error:\n{error}\n" +
                "Return the SAME JSON shape again, fixing only the SQL so it is a single valid read-only SELECT " +
                "using only the provided tables and columns."),
        };
        var options = new ChatCompletionOptions { Temperature = 0f, ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
        var response = await chatClient.CompleteChatAsync(fixMessages, options, ct);
        var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : null;
        if (string.IsNullOrWhiteSpace(content)) return null;

        var parsed = Parse(content);
        // Match the fixed op back to the one we're repairing (by widgetId when updating, else the first add/update).
        return parsed.Operations.FirstOrDefault(o =>
                   o.Type is DashboardOpType.AddWidget or DashboardOpType.UpdateWidget &&
                   (op.WidgetId == null || o.WidgetId == op.WidgetId))
               ?? parsed.Operations.FirstOrDefault(o => o.Type is DashboardOpType.AddWidget or DashboardOpType.UpdateWidget);
    }

    // ---- schema context ----

    private async Task<string> GetSchemaContextAsync(Dataset dataset, string companyId, CancellationToken ct)
    {
        if (SchemaCache.TryGetValue(dataset.Id!, out var cached) && DateTime.UtcNow - cached.CachedAt < SchemaTtl)
            return cached.Schema;

        var sb = new StringBuilder();
        if (dataset.SourceType == DatasetSourceType.External && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            await AppendExternalSchemaAsync(sb, dataset, companyId, ct);
        else
            await AppendLocalSchemaAsync(sb, dataset, ct);

        var schema = sb.ToString();
        SchemaCache[dataset.Id!] = (DateTime.UtcNow, schema);
        return schema;
    }

    private async Task AppendLocalSchemaAsync(StringBuilder sb, Dataset dataset, CancellationToken ct)
    {
        var tables = (await _duckdbService.GetTablesAsync(dataset.Id!)).Take(MaxSchemaTables).ToList();
        foreach (var table in tables)
        {
            var columns = await _duckdbService.GetTableColumnsAsync(dataset.Id!, table);
            sb.AppendLine($"Table \"{table}\":");
            foreach (var c in columns)
                sb.AppendLine($"  - {c.Name} ({c.DataType})");
            sb.AppendLine();
        }
    }

    private async Task AppendExternalSchemaAsync(StringBuilder sb, Dataset dataset, string companyId, CancellationToken ct)
    {
        var discovery = await _databaseTableService.DiscoverTablesAsync(dataset.SourceEntityId!, companyId, ct);
        var tables = discovery.Tables.Take(MaxSchemaTables).ToList();
        foreach (var t in tables)
        {
            // The source discovery doesn't include columns; a tiny bounded SELECT gives us the column shape.
            var preview = await _databaseTableService.ExecuteQueryAsync(dataset.SourceEntityId!, companyId,
                $"SELECT * FROM {t.FullName} LIMIT {SampleRows}", SampleRows, ct);
            sb.AppendLine($"Table \"{t.FullName}\":");
            if (preview.Error == null)
                foreach (var c in preview.Columns)
                    sb.AppendLine($"  - {c.Name} ({c.DataType})");
            sb.AppendLine();
        }
    }

    // ---- prompt + parsing ----

    private static string BuildSystemPrompt(string schema, List<DashboardWidgetContext> existing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a data-dashboard building assistant. The user describes a dashboard in natural language;");
        sb.AppendLine("you respond ONLY with a JSON object describing operations to apply to the dashboard.");
        sb.AppendLine();
        sb.AppendLine("Available tables and columns (use these names EXACTLY; never invent columns):");
        sb.AppendLine(string.IsNullOrWhiteSpace(schema) ? "(no tables found)" : schema);
        sb.AppendLine("Current widgets on the dashboard:");
        if (existing.Count == 0)
            sb.AppendLine("(none)");
        else
            foreach (var w in existing)
                sb.AppendLine($"  - id={w.Id} | title=\"{w.Title}\" | viz={w.VizType} | sql={w.Sql}");
        sb.AppendLine();
        sb.AppendLine("SQL rules (STRICT):");
        sb.AppendLine("- Each widget's `sql` MUST be a SINGLE read-only SELECT (it may start with WITH).");
        sb.AppendLine("- No INSERT/UPDATE/DELETE/DROP/CREATE/ALTER, no multiple statements, no comments.");
        sb.AppendLine("- Reference only the tables and columns listed above. Aggregate in SQL (GROUP BY) as needed.");
        sb.AppendLine("- The application enforces read-only access and a row cap; do not add tricks around them.");
        sb.AppendLine();
        sb.AppendLine("Visualization types: \"table\", \"bar\", \"line\", \"area\", \"pie\", \"doughnut\", \"kpi\".");
        sb.AppendLine("- bar/line/area: the query should return a category/x column and a numeric value column;");
        sb.AppendLine("  set config.xField and config.valueField (and config.seriesField if grouping into series).");
        sb.AppendLine("- pie/doughnut: return a label column and one numeric value column; set config.xField (label)");
        sb.AppendLine("  and config.valueField (numeric). Keep the number of categories small (e.g. top 10).");
        sb.AppendLine("- kpi: the query should return a single numeric value; set config.valueField.");
        sb.AppendLine("- table: any SELECT; config fields optional.");
        sb.AppendLine("Pick the chart type that best fits the request (e.g. trends over time -> line/area, parts of a whole -> pie/doughnut, comparisons across categories -> bar).");
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object ONLY, in this exact shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"reply\": \"<one short sentence describing what you did>\",");
        sb.AppendLine("  \"operations\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"type\": \"add_widget|update_widget|remove_widget|rename_widget|none\",");
        sb.AppendLine("      \"widgetId\": \"<required for update/remove/rename; use an id from the current widgets>\",");
        sb.AppendLine("      \"widget\": {");
        sb.AppendLine("        \"title\": \"<short title>\",");
        sb.AppendLine("        \"vizType\": \"table|bar|line|kpi\",");
        sb.AppendLine("        \"sql\": \"<single SELECT>\",");
        sb.AppendLine("        \"xField\": \"<column>\", \"valueField\": \"<column>\", \"seriesField\": null");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("Use type \"none\" (empty operations) if the request isn't actionable. For remove/rename, `widget` may be omitted (rename uses widget.title). Do not add commentary outside the JSON.");
        return sb.ToString();
    }

    private DashboardAgentResult Parse(string content)
    {
        var result = new DashboardAgentResult();
        try
        {
            using var doc = JsonDocument.Parse(StripCodeFences(content));
            var root = doc.RootElement;
            if (root.TryGetProperty("reply", out var replyEl) && replyEl.ValueKind == JsonValueKind.String)
                result.Reply = replyEl.GetString() ?? string.Empty;

            if (root.TryGetProperty("operations", out var opsEl) && opsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var opEl in opsEl.EnumerateArray())
                {
                    if (opEl.ValueKind != JsonValueKind.Object) continue;
                    var op = new DashboardOperation
                    {
                        Type = ParseOpType(GetString(opEl, "type")),
                        WidgetId = GetString(opEl, "widgetId"),
                    };

                    if (opEl.TryGetProperty("widget", out var w) && w.ValueKind == JsonValueKind.Object)
                    {
                        op.Title = GetString(w, "title");
                        op.VizType = GetString(w, "vizType");
                        op.Sql = GetString(w, "sql");
                        op.Config = new WidgetConfig
                        {
                            XField = GetString(w, "xField"),
                            SeriesField = GetString(w, "seriesField"),
                            ValueField = GetString(w, "valueField"),
                            Aggregate = GetString(w, "aggregate"),
                            NumberFormat = GetString(w, "numberFormat"),
                        };
                    }
                    result.Operations.Add(op);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[DashboardAgent] Could not parse model response: {Content}", content);
            if (string.IsNullOrEmpty(result.Reply))
                result.Reply = "I couldn't understand the plan for that request. Please try rephrasing.";
        }
        return result;
    }

    private static DashboardOpType ParseOpType(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "add_widget" => DashboardOpType.AddWidget,
        "update_widget" => DashboardOpType.UpdateWidget,
        "remove_widget" => DashboardOpType.RemoveWidget,
        "rename_widget" => DashboardOpType.RenameWidget,
        _ => DashboardOpType.None,
    };

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string StripCodeFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0) trimmed = trimmed.Substring(firstNewline + 1);
        if (trimmed.EndsWith("```")) trimmed = trimmed.Substring(0, trimmed.Length - 3);
        return trimmed.Trim();
    }
}

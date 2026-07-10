using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.Notebooks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Application.Shared.Services.Data;

public class NotebookPriorCellContext
{
    public string? Name { get; set; }
    public string CellType { get; set; } = "sql";
    public string? Sql { get; set; }
}

public class NotebookReferencedCellContext
{
    public string Name { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
}

public class NotebookAgentRequest
{
    public string CompanyId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<NotebookPriorCellContext> PriorCells { get; set; } = new();
    public List<NotebookReferencedCellContext> ReferencedCells { get; set; } = new();
}

public interface INotebookAgentService
{
    /// <summary>
    /// Turns a natural-language request into a suggested SQL cell body, grounded in the cell's dataset
    /// schema plus the notebook's prior/referenced cells. Only ever proposes SQL — never executes or
    /// materializes anything; the user reviews it in the editor before running. Never throws.
    /// </summary>
    Task<NotebookAiAssistResult> SuggestSqlAsync(NotebookAgentRequest request, CancellationToken ct = default);
}

public class NotebookAgentService : INotebookAgentService
{
    private const int MaxSchemaTables = 15;
    private const int SampleRows = 3;

    private readonly AzureOpenAIConfiguration _config;
    private readonly AzureOpenAIClient _client;
    private readonly IDatasetService _datasetService;
    private readonly IDuckdbService _duckdbService;
    private readonly IDatabaseTableService _databaseTableService;
    private readonly ILogger<NotebookAgentService> _logger;

    public NotebookAgentService(
        IOptions<AzureOpenAIConfiguration> config,
        IDatasetService datasetService,
        IDuckdbService duckdbService,
        IDatabaseTableService databaseTableService,
        ILogger<NotebookAgentService> logger)
    {
        _config = config.Value;
        _client = new AzureOpenAIClient(new Uri(_config.Endpoint), new AzureKeyCredential(_config.ApiKey));
        _datasetService = datasetService;
        _duckdbService = duckdbService;
        _databaseTableService = databaseTableService;
        _logger = logger;
    }

    public async Task<NotebookAiAssistResult> SuggestSqlAsync(NotebookAgentRequest request, CancellationToken ct = default)
    {
        var result = new NotebookAiAssistResult();

        var dataset = await _datasetService.GetDatasetAsync(request.DatasetId, request.UserId);
        if (dataset == null)
        {
            result.Reply = "I couldn't find this cell's dataset.";
            return result;
        }

        string schema;
        try
        {
            schema = await GetSchemaContextAsync(dataset, request.CompanyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NotebookAgent] Failed to build schema context for dataset {DatasetId}.", request.DatasetId);
            result.Reply = "I couldn't read this dataset's schema, so I can't suggest SQL right now.";
            return result;
        }

        var referencedSchema = await BuildReferencedCellsSchemaAsync(request.ReferencedCells, ct);

        try
        {
            var chatClient = _client.GetChatClient(_config.DeploymentName);
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(BuildSystemPrompt(schema, referencedSchema, request.PriorCells)),
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

            Parse(content, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Reply = "The request timed out. Please try again.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NotebookAgent] Suggestion failed for dataset {DatasetId}.", request.DatasetId);
            result.Reply = "Something went wrong while suggesting SQL. Please try again.";
            return result;
        }
    }

    // ---- schema context ----

    private async Task<string> GetSchemaContextAsync(Dataset dataset, string companyId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (dataset.SourceType == DatasetSourceType.External && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            await AppendExternalSchemaAsync(sb, dataset, companyId, ct);
        else
            await AppendLocalSchemaAsync(sb, dataset, ct);
        return sb.ToString();
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
            var preview = await _databaseTableService.ExecuteQueryAsync(dataset.SourceEntityId!, companyId,
                $"SELECT * FROM {t.FullName} LIMIT {SampleRows}", SampleRows, ct);
            sb.AppendLine($"Table \"{t.FullName}\":");
            if (preview.Error == null)
                foreach (var c in preview.Columns)
                    sb.AppendLine($"  - {c.Name} ({c.DataType})");
            sb.AppendLine();
        }
    }

    // Referenced cells' materialized objects always live in DuckDB (even ones originally sourced from an
    // External live query get snapshotted before they're referenceable) — so their columns are always
    // readable via GetTableColumnsAsync regardless of the original source kind.
    private async Task<string> BuildReferencedCellsSchemaAsync(List<NotebookReferencedCellContext> referenced, CancellationToken ct)
    {
        if (referenced.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var r in referenced)
        {
            try
            {
                var columns = await _duckdbService.GetTableColumnsAsync(r.DatasetId, r.Name);
                sb.AppendLine($"Cell reference \"{r.Name}\" (from another notebook cell):");
                foreach (var c in columns)
                    sb.AppendLine($"  - {c.Name} ({c.DataType})");
                sb.AppendLine();
            }
            catch { /* best-effort — omit if unreadable */ }
        }
        return sb.ToString();
    }

    // ---- prompt + parsing ----

    private static string BuildSystemPrompt(string schema, string referencedSchema, List<NotebookPriorCellContext> priorCells)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a SQL notebook assistant. The user describes what they want in natural language;");
        sb.AppendLine("you respond ONLY with a JSON object containing a short reply and a single suggested SQL query.");
        sb.AppendLine();
        sb.AppendLine("Available tables and columns in this cell's dataset (use these names EXACTLY; never invent columns):");
        sb.AppendLine(string.IsNullOrWhiteSpace(schema) ? "(no tables found)" : schema);

        if (!string.IsNullOrWhiteSpace(referencedSchema))
        {
            sb.AppendLine("Other notebook cells this one references (their result is available as a table by this name):");
            sb.AppendLine(referencedSchema);
        }

        if (priorCells.Count > 0)
        {
            sb.AppendLine("Earlier cells in this notebook, for context on what's already been computed:");
            foreach (var c in priorCells)
            {
                if (c.CellType == "sql" && !string.IsNullOrWhiteSpace(c.Sql))
                    sb.AppendLine($"  - {(string.IsNullOrWhiteSpace(c.Name) ? "(unnamed)" : c.Name)}: {c.Sql}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("SQL rules (STRICT):");
        sb.AppendLine("- The suggested `sql` MUST be a SINGLE read-only SELECT (it may start with WITH).");
        sb.AppendLine("- No INSERT/UPDATE/DELETE/DROP/CREATE/ALTER, no multiple statements, no comments.");
        sb.AppendLine("- Reference only the tables/columns and cell references listed above.");
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object ONLY, in this exact shape:");
        sb.AppendLine("{ \"reply\": \"<one short sentence>\", \"sql\": \"<single SELECT, or null if the request isn't actionable>\" }");
        sb.AppendLine("Do not add commentary outside the JSON.");
        return sb.ToString();
    }

    private void Parse(string content, NotebookAiAssistResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripCodeFences(content));
            var root = doc.RootElement;
            if (root.TryGetProperty("reply", out var replyEl) && replyEl.ValueKind == JsonValueKind.String)
                result.Reply = replyEl.GetString() ?? string.Empty;
            if (root.TryGetProperty("sql", out var sqlEl) && sqlEl.ValueKind == JsonValueKind.String)
                result.Sql = sqlEl.GetString();

            if (string.IsNullOrWhiteSpace(result.Reply))
                result.Reply = result.Sql != null ? "Here's a suggested query." : "Could you give me a bit more detail?";
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[NotebookAgent] Could not parse model response: {Content}", content);
            result.Reply = "I couldn't understand that. Please try rephrasing.";
        }
    }

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

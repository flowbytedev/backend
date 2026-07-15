using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Services;
using Application.Shared.Services.Logging;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Application.Shared.Services.Data;

public class ColumnDocGenerationResult
{
    public int ColumnsDocumented { get; set; }
    public string? Error { get; set; }
}

public interface IColumnDocGenerationService
{
    /// <summary>
    /// Samples a table's columns + a few sanitized rows and asks Azure OpenAI to propose a description,
    /// display name, semantic type/unit and a PII flag per column, then persists them as AI-generated
    /// docs. When <paramref name="snapshotMode"/> is true the table is read from the dataset's DuckDB
    /// snapshot; when false it is read from the External dataset's live source. Never throws — failures
    /// are returned via <see cref="ColumnDocGenerationResult.Error"/>.
    /// </summary>
    Task<ColumnDocGenerationResult> GenerateAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default);
}

public class ColumnDocGenerationService : IColumnDocGenerationService
{
    // Cap what we send so a wide/long table can't blow up the prompt (and cost) — and to limit PII exposure.
    private const int MaxSampleRows = 10;
    private const int MaxCellLength = 120;

    private readonly AzureOpenAIConfiguration _config;
    private readonly AzureOpenAIClient _client;
    private readonly IDuckdbService _duckdb;
    private readonly IDatabaseTableService _dbTables;
    private readonly ApplicationDbContext _db;
    private readonly IDatasetDocService _docService;
    private readonly ILogger<ColumnDocGenerationService> _logger;
    private readonly IDebugLogService _debug;

    public ColumnDocGenerationService(
        IOptions<AzureOpenAIConfiguration> config,
        IDuckdbService duckdb,
        IDatabaseTableService dbTables,
        ApplicationDbContext db,
        IDatasetDocService docService,
        ILogger<ColumnDocGenerationService> logger,
        IDebugLogService debug)
    {
        _config = config.Value;
        _client = new AzureOpenAIClient(new Uri(_config.Endpoint), new AzureKeyCredential(_config.ApiKey));
        _duckdb = duckdb;
        _dbTables = dbTables;
        _db = db;
        _docService = docService;
        _logger = logger;
        _debug = debug;
    }

    public async Task<ColumnDocGenerationResult> GenerateAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default)
    {
        var result = new ColumnDocGenerationResult();

        List<Column> columns;
        try
        {
            columns = await _docService.GetLiveColumnsAsync(companyId, datasetId, tableName, snapshotMode, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ColumnDoc] Failed to read columns for {Dataset}/{Table}.", datasetId, tableName);
            await _debug.LogAsync(companyId, DebugLevel.Error, "DataDocs",
                $"Couldn't read columns for '{tableName}': {ex.Message}",
                datasetId: datasetId, tableName: tableName, error: ex.Message, ct: ct);
            result.Error = "Couldn't read the table's columns.";
            return result;
        }

        if (columns.Count == 0)
        {
            await _debug.LogAsync(companyId, DebugLevel.Warn, "DataDocs",
                $"Nothing to document for '{tableName}': the table has no columns.",
                datasetId: datasetId, tableName: tableName, ct: ct);
            result.Error = "This table has no columns to document.";
            return result;
        }

        await _debug.LogAsync(companyId, DebugLevel.Debug, "DataDocs",
            $"Documenting '{tableName}': {columns.Count} column(s), snapshot={snapshotMode}.",
            datasetId: datasetId, tableName: tableName,
            context: new { columns = columns.Count, snapshotMode }, ct: ct);

        // A small, read-only sample gives the model real values to reason about. Failure here is
        // non-fatal — we can still describe from names + types. In snapshot mode we read the dataset's
        // DuckDB copy; in source mode we read the External dataset's live source table.
        List<List<string?>> sampleRows = new();
        try
        {
            SqlQueryResult? sample = null;
            if (snapshotMode)
            {
                var quoted = "\"" + tableName.Replace("\"", "\"\"") + "\"";
                sample = await _duckdb.ExecuteSqlAsync(datasetId, $"SELECT * FROM {quoted} LIMIT {MaxSampleRows}", allowWrite: false, maxRows: MaxSampleRows, ct);
            }
            else
            {
                var dataset = await _db.Dataset.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == datasetId && d.CompanyId == companyId, ct);
                if (dataset?.SourceType == DatasetSourceType.External && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
                    // Server-side TOP/LIMIT so a heavy source view isn't fully evaluated just for a few rows.
                    sample = await _dbTables.GetTableSampleAsync(dataset.SourceEntityId!, companyId, tableName, MaxSampleRows, ct);
            }

            if (sample != null && sample.Error == null)
                sampleRows = sample.Rows.Select(r => columns.Select(c => r.TryGetValue(c.Name, out var v) ? v?.ToString() : null).ToList()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ColumnDoc] Sample fetch failed for {Dataset}/{Table}; continuing without samples.", datasetId, tableName);
            await _debug.LogAsync(companyId, DebugLevel.Warn, "DataDocs",
                $"Sample fetch failed for '{tableName}'; continuing without samples: {ex.Message}",
                datasetId: datasetId, tableName: tableName, error: ex.Message, ct: ct);
        }

        await _debug.LogAsync(companyId, DebugLevel.Debug, "DataDocs",
            $"Sample fetch for '{tableName}': {sampleRows.Count} row(s) collected.",
            datasetId: datasetId, tableName: tableName,
            context: new { sampleRows = sampleRows.Count }, ct: ct);

        try
        {
            var chatClient = _client.GetChatClient(_config.DeploymentName);
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(BuildSystemPrompt()),
                new UserChatMessage(BuildUserPrompt(tableName, columns, sampleRows)),
            };
            var options = new ChatCompletionOptions
            {
                Temperature = 0f,
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Documenting a wide table means the model must emit JSON for every column, which can take a
            // while — allow more headroom than the interactive agents, but stay under the Blazor client's
            // ~100s HttpClient timeout so the browser doesn't abort the request first.
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_config.TimeoutSeconds, 90)));

            var sw = Stopwatch.StartNew();
            var response = await chatClient.CompleteChatAsync(messages, options, cts.Token);
            sw.Stop();

            await _debug.LogAsync(companyId, DebugLevel.Info, "DataDocs",
                $"Azure OpenAI ({_config.DeploymentName}) responded in {sw.ElapsedMilliseconds} ms for '{tableName}'.",
                datasetId: datasetId, tableName: tableName, durationMs: sw.ElapsedMilliseconds,
                context: new { deployment = _config.DeploymentName }, ct: ct);

            var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                await _debug.LogAsync(companyId, DebugLevel.Error, "DataDocs",
                    $"AI returned an empty response for '{tableName}'.",
                    datasetId: datasetId, tableName: tableName, ct: ct);
                result.Error = "The AI service returned an empty response.";
                return result;
            }

            var docs = ParseDocs(content, columns);
            if (docs.Count == 0)
            {
                await _debug.LogAsync(companyId, DebugLevel.Warn, "DataDocs",
                    $"AI response for '{tableName}' contained no usable column documentation.",
                    datasetId: datasetId, tableName: tableName, ct: ct);
                result.Error = "The AI service didn't return any usable column documentation.";
                return result;
            }

            await _docService.ApplyGeneratedDocsAsync(companyId, datasetId, tableName, snapshotMode, docs, ct);
            result.ColumnsDocumented = docs.Count;

            await _debug.LogAsync(companyId, DebugLevel.Info, "DataDocs",
                $"Applied {docs.Count} generated column doc(s) for '{tableName}'.",
                datasetId: datasetId, tableName: tableName, context: new { documented = docs.Count }, ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            await _debug.LogAsync(companyId, DebugLevel.Warn, "DataDocs",
                $"AI request timed out for '{tableName}'.",
                datasetId: datasetId, tableName: tableName, ct: ct);
            result.Error = "The AI request timed out. Please try again.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ColumnDoc] Generation failed for {Dataset}/{Table}.", datasetId, tableName);
            await _debug.LogAsync(companyId, DebugLevel.Error, "DataDocs",
                $"Generation failed for '{tableName}': {ex.GetType().Name}: {ex.Message}",
                datasetId: datasetId, tableName: tableName, error: ex.Message, ct: ct);
            result.Error = $"AI service error: {ex.GetType().Name}: {ex.Message}";
            return result;
        }
    }

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a data-catalog assistant. Given a database table's column names, types and a small sample of rows,");
        sb.AppendLine("you document each column so analysts and query-writing tools understand it.");
        sb.AppendLine();
        sb.AppendLine("For EACH column provide:");
        sb.AppendLine("- displayName: a short, human-friendly label (Title Case), e.g. \"Net Amount\" for net_amt_acy.");
        sb.AppendLine("- description: one concise sentence explaining what the column holds. Use the sample values as evidence; don't invent facts.");
        sb.AppendLine("- semanticType: one of currency, percentage, date, datetime, time, email, phone, name, address, url, identifier, quantity, count, category, boolean, text, other.");
        sb.AppendLine("- unit: a unit when clearly applicable (e.g. \"USD\", \"kg\", \"%\", \"days\"), otherwise null.");
        sb.AppendLine("- isPii: true if the column likely contains personal data (names, emails, phone numbers, addresses, national ids). This is an advisory hint for human review.");
        sb.AppendLine("- piiType: when isPii is true, a short label like \"email\", \"name\", \"phone\", \"address\"; otherwise null.");
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object ONLY, in this exact shape:");
        sb.AppendLine("{ \"columns\": [ { \"name\": \"<column name>\", \"displayName\": \"...\", \"description\": \"...\", \"semanticType\": \"...\", \"unit\": null, \"isPii\": false, \"piiType\": null } ] }");
        sb.AppendLine("Use the column name EXACTLY as given. Include every column once. Do not add commentary outside the JSON.");
        return sb.ToString();
    }

    private static string BuildUserPrompt(string tableName, List<Column> columns, List<List<string?>> sampleRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Table: {tableName}");
        sb.AppendLine("Columns (name and type):");
        foreach (var c in columns)
            sb.AppendLine($"- {c.Name} ({c.DataType})");

        sb.AppendLine();
        sb.AppendLine($"Sample rows (up to {MaxSampleRows}), values aligned to the column order above:");
        if (sampleRows.Count == 0)
        {
            sb.AppendLine("(no sample rows available)");
        }
        else
        {
            foreach (var row in sampleRows.Take(MaxSampleRows))
            {
                var cells = new List<string>();
                for (var i = 0; i < columns.Count; i++)
                    cells.Add(Clip(i < row.Count ? row[i] : null));
                // JSON-array form keeps cell boundaries unambiguous even when values contain commas.
                sb.AppendLine(JsonSerializer.Serialize(cells));
            }
        }
        return sb.ToString();
    }

    private static string Clip(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var v = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return v.Length > MaxCellLength ? v.Substring(0, MaxCellLength) : v;
    }

    private List<SaveColumnDocRequest> ParseDocs(string content, List<Column> columns)
    {
        var docs = new List<SaveColumnDocRequest>();
        var known = new HashSet<string>(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(StripCodeFences(content));
            if (!doc.RootElement.TryGetProperty("columns", out var columnsEl) || columnsEl.ValueKind != JsonValueKind.Array)
                return docs;

            foreach (var item in columnsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(name) || !known.Contains(name) || !seen.Add(name)) continue;

                docs.Add(new SaveColumnDocRequest
                {
                    ColumnName = name,
                    DisplayName = GetString(item, "displayName"),
                    Description = GetString(item, "description"),
                    SemanticType = GetString(item, "semanticType"),
                    Unit = GetString(item, "unit"),
                    IsPii = GetBool(item, "isPii"),
                    PiiType = GetString(item, "piiType"),
                });
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[ColumnDoc] Could not parse generation response: {Content}", content);
        }
        return docs;
    }

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

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

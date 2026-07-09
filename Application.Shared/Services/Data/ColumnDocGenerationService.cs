using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Azure;
using Azure.AI.OpenAI;
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
    /// docs. Never throws — failures are returned via <see cref="ColumnDocGenerationResult.Error"/>.
    /// </summary>
    Task<ColumnDocGenerationResult> GenerateAsync(string companyId, string datasetId, string tableName, CancellationToken ct = default);
}

public class ColumnDocGenerationService : IColumnDocGenerationService
{
    // Cap what we send so a wide/long table can't blow up the prompt (and cost) — and to limit PII exposure.
    private const int MaxSampleRows = 10;
    private const int MaxCellLength = 120;

    private readonly AzureOpenAIConfiguration _config;
    private readonly AzureOpenAIClient _client;
    private readonly IDuckdbService _duckdb;
    private readonly IDatasetDocService _docService;
    private readonly ILogger<ColumnDocGenerationService> _logger;

    public ColumnDocGenerationService(
        IOptions<AzureOpenAIConfiguration> config,
        IDuckdbService duckdb,
        IDatasetDocService docService,
        ILogger<ColumnDocGenerationService> logger)
    {
        _config = config.Value;
        _client = new AzureOpenAIClient(new Uri(_config.Endpoint), new AzureKeyCredential(_config.ApiKey));
        _duckdb = duckdb;
        _docService = docService;
        _logger = logger;
    }

    public async Task<ColumnDocGenerationResult> GenerateAsync(string companyId, string datasetId, string tableName, CancellationToken ct = default)
    {
        var result = new ColumnDocGenerationResult();

        List<Column> columns;
        try
        {
            columns = await _duckdb.GetTableColumnsAsync(datasetId, tableName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ColumnDoc] Failed to read columns for {Dataset}/{Table}.", datasetId, tableName);
            result.Error = "Couldn't read the table's columns.";
            return result;
        }

        if (columns.Count == 0)
        {
            result.Error = "This table has no columns to document.";
            return result;
        }

        // A small, read-only sample gives the model real values to reason about. Failure here is
        // non-fatal — we can still describe from names + types.
        List<List<string?>> sampleRows = new();
        try
        {
            var quoted = "\"" + tableName.Replace("\"", "\"\"") + "\"";
            var sample = await _duckdb.ExecuteSqlAsync(datasetId, $"SELECT * FROM {quoted} LIMIT {MaxSampleRows}", allowWrite: false, maxRows: MaxSampleRows, ct);
            if (sample.Error == null)
                sampleRows = sample.Rows.Select(r => columns.Select(c => r.TryGetValue(c.Name, out var v) ? v?.ToString() : null).ToList()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ColumnDoc] Sample fetch failed for {Dataset}/{Table}; continuing without samples.", datasetId, tableName);
        }

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
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_config.TimeoutSeconds, 45)));

            var response = await chatClient.CompleteChatAsync(messages, options, cts.Token);
            var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                result.Error = "The AI service returned an empty response.";
                return result;
            }

            var docs = ParseDocs(content, columns);
            if (docs.Count == 0)
            {
                result.Error = "The AI service didn't return any usable column documentation.";
                return result;
            }

            await _docService.ApplyGeneratedDocsAsync(companyId, datasetId, tableName, docs, ct);
            result.ColumnsDocumented = docs.Count;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "The AI request timed out. Please try again.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ColumnDoc] Generation failed for {Dataset}/{Table}.", datasetId, tableName);
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

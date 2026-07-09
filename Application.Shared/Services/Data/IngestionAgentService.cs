using System;
using System.Collections.Concurrent;
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

/// <summary>One prior chat turn passed back so the agent refines rather than restarts.</summary>
public class IngestionChatTurn
{
    public string Role { get; set; } = "user"; // "user" | "assistant"
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// A proposed ingestion source config. Mirrors <see cref="SaveIngestionSourceRequest"/> (minus the
/// secret, which the agent never handles) so the client can hand it straight to the existing create form.
/// </summary>
public class IngestionDraft
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? TargetTable { get; set; }
    public string SourceKind { get; set; } = "ExternalDatabase";
    public string? SourceEntityId { get; set; }
    public IngestionSourceConfig Config { get; set; } = new();
    public string ImportMode { get; set; } = "Append";
    public List<string> KeyColumns { get; set; } = new();
    public bool CreateIfNotExists { get; set; } = true;
    public string? IncrementalColumn { get; set; }
    public string CronExpression { get; set; } = "0 * * * *";
    public string? TimeZone { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class IngestionAgentRequest
{
    public string DatasetId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<IngestionChatTurn> Conversation { get; set; } = new();
    public IngestionDraft? CurrentDraft { get; set; }
}

public class IngestionAgentResult
{
    public string Reply { get; set; } = string.Empty;
    public IngestionDraft? Draft { get; set; }
    public List<string> MissingInfo { get; set; } = new();
}

public interface IIngestionAgentService
{
    /// <summary>
    /// Turns a natural-language description (with prior turns + the current draft) into a refined
    /// ingestion source draft, grounded in the company's connected databases and their tables. Only
    /// proposes — never persists, never proposes secrets. Never throws.
    /// </summary>
    Task<IngestionAgentResult> PlanAsync(IngestionAgentRequest request, CancellationToken ct = default);
}

public class IngestionAgentService : IIngestionAgentService
{
    private const int MaxTables = 60;

    // Short-lived cache of discovered source tables per (company, entity) — discovery hits the live DB.
    private static readonly ConcurrentDictionary<string, (DateTime CachedAt, List<string> Tables)> TableCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly AzureOpenAIConfiguration _config;
    private readonly AzureOpenAIClient _client;
    private readonly IDatabaseTableService _databaseTableService;
    private readonly IDuckdbService _duckdbService;
    private readonly ILogger<IngestionAgentService> _logger;

    public IngestionAgentService(
        IOptions<AzureOpenAIConfiguration> config,
        IDatabaseTableService databaseTableService,
        IDuckdbService duckdbService,
        ILogger<IngestionAgentService> logger)
    {
        _config = config.Value;
        _client = new AzureOpenAIClient(new Uri(_config.Endpoint), new AzureKeyCredential(_config.ApiKey));
        _databaseTableService = databaseTableService;
        _duckdbService = duckdbService;
        _logger = logger;
    }

    public async Task<IngestionAgentResult> PlanAsync(IngestionAgentRequest request, CancellationToken ct = default)
    {
        var result = new IngestionAgentResult();

        // ---- discovery (grounding) ----
        List<DatabaseEntityOptionDto> databases;
        try
        {
            databases = await _databaseTableService.GetConnectedDatabasesAsync(request.CompanyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IngestionAgent] Failed to list connected databases for company {CompanyId}.", request.CompanyId);
            databases = new();
        }

        // Resolve which source entity to ground on: the one already chosen in the draft, or the only
        // connected database when there's just one (a common case).
        var entityId = !string.IsNullOrWhiteSpace(request.CurrentDraft?.SourceEntityId)
            ? request.CurrentDraft!.SourceEntityId
            : (databases.Count == 1 ? databases[0].Id : null);

        var sourceTables = entityId != null ? await GetSourceTablesAsync(entityId, request.CompanyId, ct) : new List<string>();

        List<string> targetTables;
        try { targetTables = (await _duckdbService.GetTablesAsync(request.DatasetId)).ToList(); }
        catch { targetTables = new(); }

        // ---- plan ----
        try
        {
            var chatClient = _client.GetChatClient(_config.DeploymentName);
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(BuildSystemPrompt(databases, entityId, sourceTables, targetTables, request.CurrentDraft)),
            };
            foreach (var turn in request.Conversation)
            {
                if (string.IsNullOrWhiteSpace(turn.Content)) continue;
                messages.Add(string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? new AssistantChatMessage(turn.Content)
                    : new UserChatMessage(turn.Content));
            }
            messages.Add(new UserChatMessage(request.Message));

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
            _logger.LogError(ex, "[IngestionAgent] Planning failed for dataset {DatasetId}.", request.DatasetId);
            result.Reply = "Something went wrong while drafting the ingestion job. Please try again.";
            return result;
        }
    }

    private async Task<List<string>> GetSourceTablesAsync(string entityId, string companyId, CancellationToken ct)
    {
        var key = companyId + "|" + entityId;
        if (TableCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
            return cached.Tables;

        var tables = new List<string>();
        try
        {
            var discovery = await _databaseTableService.DiscoverTablesAsync(entityId, companyId, ct);
            if (discovery.Error == null)
                tables = discovery.Tables.Select(t => t.FullName).Take(MaxTables).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IngestionAgent] Table discovery failed for entity {EntityId}.", entityId);
        }

        TableCache[key] = (DateTime.UtcNow, tables);
        return tables;
    }

    private static string BuildSystemPrompt(
        List<DatabaseEntityOptionDto> databases, string? entityId,
        List<string> sourceTables, List<string> targetTables, IngestionDraft? draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an assistant that drafts a scheduled data-ingestion job from a natural-language description.");
        sb.AppendLine("You respond ONLY with a JSON object describing a single ingestion source draft plus a short reply.");
        sb.AppendLine("The draft is a PROPOSAL the user reviews and saves manually — you never create anything yourself.");
        sb.AppendLine();
        sb.AppendLine("This assistant supports ExternalDatabase sources (pulling from a configured database). If the user");
        sb.AppendLine("clearly asks for REST/Blob/SFTP you may set sourceKind accordingly, but note in missingInfo that only");
        sb.AppendLine("database sources are grounded with real schema here.");
        sb.AppendLine();
        sb.AppendLine("Connected databases available as sources (use the id as sourceEntityId):");
        if (databases.Count == 0)
            sb.AppendLine("(none configured — tell the user to add a database connection first)");
        else
            foreach (var d in databases)
                sb.AppendLine($"  - id={d.Id} | name=\"{d.Name}\" | type={d.DatabaseType}");
        sb.AppendLine();

        if (entityId != null && sourceTables.Count > 0)
        {
            sb.AppendLine($"Tables available in the selected source database (id={entityId}) — use these exact \"schema.table\" names:");
            foreach (var t in sourceTables)
                sb.AppendLine($"  - {t}");
            sb.AppendLine();
        }
        else if (databases.Count > 1)
        {
            sb.AppendLine("No source database is selected yet. Ask the user which database (by name), set its id as sourceEntityId,");
            sb.AppendLine("and list what's missing so tables can be discovered on the next turn.");
            sb.AppendLine();
        }

        sb.AppendLine("Existing tables already in this dataset (targetTable usually matches or is a new snake_case name):");
        if (targetTables.Count == 0) sb.AppendLine("(none yet)");
        else foreach (var t in targetTables) sb.AppendLine($"  - {t}");
        sb.AppendLine();

        if (draft != null)
        {
            sb.AppendLine("Current draft so far (refine this — keep good fields, change only what the user asks):");
            sb.AppendLine(JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = false }));
            sb.AppendLine();
        }

        sb.AppendLine("Field guidance:");
        sb.AppendLine("- sourceKind: \"ExternalDatabase\" (default), \"Rest\", \"Blob\", or \"Sftp\".");
        sb.AppendLine("- For ExternalDatabase set config.schema + config.table (from the list above), OR config.query for a custom SELECT.");
        sb.AppendLine("  Optionally config.commandTimeoutSeconds and config.batchSize + config.batchKeyColumn for very large tables.");
        sb.AppendLine("- importMode: \"Append\", \"Replace\", or \"Upsert\". Upsert REQUIRES keyColumns (the unique key).");
        sb.AppendLine("- incrementalColumn: a monotonically increasing column (e.g. modified_at, id) when the user wants only new/changed rows.");
        sb.AppendLine("- cronExpression: standard 5-field cron. Examples: hourly \"0 * * * *\", nightly 2am \"0 2 * * *\", every 15m \"*/15 * * * *\".");
        sb.AppendLine("- timeZone: IANA id, default \"Asia/Beirut\" if the user doesn't specify.");
        sb.AppendLine("- targetTable + name are required; derive sensible values from the source table if the user didn't say.");
        sb.AppendLine("- NEVER include passwords, secrets, or connection strings — the user enters the secret in the form.");
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object ONLY, in this exact shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"reply\": \"<one or two short sentences: what you drafted or what you still need>\",");
        sb.AppendLine("  \"draft\": {");
        sb.AppendLine("    \"name\": \"...\", \"description\": null, \"targetTable\": \"...\",");
        sb.AppendLine("    \"sourceKind\": \"ExternalDatabase\", \"sourceEntityId\": \"...\",");
        sb.AppendLine("    \"config\": { \"query\": null, \"schema\": \"...\", \"table\": \"...\", \"commandTimeoutSeconds\": null, \"batchSize\": null, \"batchKeyColumn\": null },");
        sb.AppendLine("    \"importMode\": \"Append\", \"keyColumns\": [], \"createIfNotExists\": true,");
        sb.AppendLine("    \"incrementalColumn\": null, \"cronExpression\": \"0 * * * *\", \"timeZone\": \"Asia/Beirut\", \"isEnabled\": true");
        sb.AppendLine("  },");
        sb.AppendLine("  \"missingInfo\": [ \"<questions or unknowns the user should resolve before saving>\" ]");
        sb.AppendLine("}");
        sb.AppendLine("Always return the full draft (all fields), even when only refining one. Do not add commentary outside the JSON.");
        return sb.ToString();
    }

    private void Parse(string content, IngestionAgentResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripCodeFences(content));
            var root = doc.RootElement;

            if (root.TryGetProperty("reply", out var replyEl) && replyEl.ValueKind == JsonValueKind.String)
                result.Reply = replyEl.GetString() ?? string.Empty;

            if (root.TryGetProperty("missingInfo", out var miEl) && miEl.ValueKind == JsonValueKind.Array)
                foreach (var m in miEl.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                        result.MissingInfo.Add(m.GetString()!);

            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.Object)
                result.Draft = ParseDraft(d);

            if (string.IsNullOrWhiteSpace(result.Reply))
                result.Reply = result.Draft != null ? "Here's a draft — review the fields and adjust as needed." : "Could you give me a bit more detail?";
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[IngestionAgent] Could not parse model response: {Content}", content);
            result.Reply = "I couldn't understand that. Please try rephrasing what you'd like to ingest.";
        }
    }

    private static IngestionDraft ParseDraft(JsonElement d)
    {
        var draft = new IngestionDraft
        {
            Name = GetString(d, "name"),
            Description = GetString(d, "description"),
            TargetTable = GetString(d, "targetTable"),
            SourceKind = GetString(d, "sourceKind") ?? "ExternalDatabase",
            SourceEntityId = GetString(d, "sourceEntityId"),
            ImportMode = GetString(d, "importMode") ?? "Append",
            CreateIfNotExists = GetBool(d, "createIfNotExists", true),
            IncrementalColumn = GetString(d, "incrementalColumn"),
            CronExpression = GetString(d, "cronExpression") ?? "0 * * * *",
            TimeZone = GetString(d, "timeZone"),
            IsEnabled = GetBool(d, "isEnabled", true),
        };

        if (d.TryGetProperty("keyColumns", out var kc) && kc.ValueKind == JsonValueKind.Array)
            draft.KeyColumns = kc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        if (d.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            draft.Config = new IngestionSourceConfig
            {
                Query = GetString(c, "query"),
                Schema = GetString(c, "schema"),
                Table = GetString(c, "table"),
                CommandTimeoutSeconds = GetInt(c, "commandTimeoutSeconds"),
                BatchSize = GetInt(c, "batchSize"),
                BatchKeyColumn = GetString(c, "batchKeyColumn"),
                Url = GetString(c, "url"),
                Method = GetString(c, "method"),
                JsonPath = GetString(c, "jsonPath"),
                AuthType = GetString(c, "authType"),
                Username = GetString(c, "username"),
                Container = GetString(c, "container"),
                BlobPath = GetString(c, "blobPath"),
                Host = GetString(c, "host"),
                Port = GetInt(c, "port"),
                SftpUsername = GetString(c, "sftpUsername"),
                RemotePath = GetString(c, "remotePath"),
                FileFormat = GetString(c, "fileFormat"),
            };
        }
        return draft;
    }

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string prop, bool fallback)
    {
        if (!el.TryGetProperty(prop, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int? GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Data;
using Application.Shared.Models.Data;
using Application.Shared.Services;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Application.Shared.Services.Data;

/// <summary>
/// Executes and manages scheduled ingestion sources: fetches data from an external DB / REST API /
/// Blob / SFTP into a temp file, then loads it into the dataset's DuckDB table via the shared import
/// core. Used by both the web app ("Run now") and the Hangfire scheduler job, so it lives in Shared.
/// </summary>
public interface IIngestionService
{
    Task<List<IngestionSourceDto>> GetSourcesAsync(string companyId, string datasetId, CancellationToken ct = default);

    /// <summary>All ingestion sources for the company across every dataset (the ingestion page lists these
    /// up front; the dataset is only a client-side filter).</summary>
    Task<List<IngestionSourceDto>> GetAllSourcesAsync(string companyId, CancellationToken ct = default);

    Task<IngestionSourceDto?> GetSourceAsync(string companyId, string id, CancellationToken ct = default);
    Task<IngestionSourceDto> CreateAsync(string companyId, string datasetId, string? userId, SaveIngestionSourceRequest request, CancellationToken ct = default);
    Task<IngestionSourceDto?> UpdateAsync(string companyId, string id, SaveIngestionSourceRequest request, CancellationToken ct = default);

    /// <summary>Pauses (<paramref name="enabled"/>=false) or resumes a source by toggling its scheduled
    /// flag. Returns the updated DTO (or null if not found). The caller reflects the change in Hangfire.</summary>
    Task<IngestionSourceDto?> SetEnabledAsync(string companyId, string id, bool enabled, CancellationToken ct = default);

    Task<bool> DeleteAsync(string companyId, string id, CancellationToken ct = default);
    Task<List<IngestionRunDto>> GetRunsAsync(string companyId, string sourceId, int limit = 20, CancellationToken ct = default);

    /// <summary>Runs one ingestion source end-to-end. Never throws — failures are captured in the run record.
    /// When <paramref name="runId"/> matches an existing (e.g. "Queued") run it is reused/transitioned;
    /// otherwise a new run is created. <paramref name="jobId"/> records the Hangfire job id when applicable.
    /// <paramref name="progress"/> (optional) receives log lines + progress for the Hangfire dashboard.</summary>
    Task<ImportResult> RunSourceAsync(string sourceId, string? runId = null, string? jobId = null, IJobProgress? progress = null, CancellationToken ct = default);

    /// <summary>Creates a placeholder run in the <c>Queued</c> state (before a Hangfire batch job starts)
    /// and returns its id, so the UI shows the queued run immediately and the job id can be attached.</summary>
    Task<string> CreateQueuedRunAsync(string companyId, string sourceId, CancellationToken ct = default);

    /// <summary>Attaches a Hangfire job id to an existing run via a targeted column update (no row clobber).</summary>
    Task SetRunJobIdAsync(string runId, string jobId, CancellationToken ct = default);

    /// <summary>Reconciles orphaned run records: marks any run still flagged <c>Running</c> whose start is
    /// older than <paramref name="olderThan"/> as <c>Failed</c>. A "Run now" executes inline in the web
    /// request, so a process restart mid-run leaves its run row stuck at Running — this cleans those up.
    /// Returns the number of runs reconciled.</summary>
    Task<int> FailStaleRunsAsync(TimeSpan olderThan, CancellationToken ct = default);

    /// <summary>User-initiated, immediate reconcile of a single source: marks its <c>Running</c> runs as
    /// <c>Failed</c> regardless of age (the user has confirmed the run is stuck). Company-scoped.
    /// Returns the number of runs cleared.</summary>
    Task<int> FailRunningRunsForSourceAsync(string companyId, string sourceId, CancellationToken ct = default);

    /// <summary>One-off snapshot: runs a read-only SELECT against a connected Database entity and materializes
    /// the rows into <paramref name="targetTable"/> in the dataset's DuckDB file (replace + create-if-missing).
    /// Used by the workbench's "Save data" for external datasets. Never throws.</summary>
    Task<ImportResult> SnapshotQueryAsync(string companyId, string datasetId, string sourceEntityId, string sql, string targetTable, CancellationToken ct = default);
}

public class IngestionService : IIngestionService
{
    private readonly ApplicationDbContext _db;
    private readonly IDuckdbService _duckdb;
    private readonly IDatabaseTableService _dbTables;
    private readonly ICredentialProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IngestionService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public IngestionService(
        ApplicationDbContext db,
        IDuckdbService duckdb,
        IDatabaseTableService dbTables,
        ICredentialProtector protector,
        IHttpClientFactory httpClientFactory,
        ILogger<IngestionService> logger)
    {
        _db = db;
        _duckdb = duckdb;
        _dbTables = dbTables;
        _protector = protector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ---- CRUD ----

    public async Task<List<IngestionSourceDto>> GetSourcesAsync(string companyId, string datasetId, CancellationToken ct = default)
    {
        var sources = await _db.IngestionSource
            .Where(s => s.CompanyId == companyId && s.DatasetId == datasetId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return sources.Select(ToDto).ToList();
    }

    public async Task<List<IngestionSourceDto>> GetAllSourcesAsync(string companyId, CancellationToken ct = default)
    {
        var sources = await _db.IngestionSource
            .Where(s => s.CompanyId == companyId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return sources.Select(ToDto).ToList();
    }

    public async Task<IngestionSourceDto?> GetSourceAsync(string companyId, string id, CancellationToken ct = default)
    {
        var source = await _db.IngestionSource.FirstOrDefaultAsync(s => s.Id == id && s.CompanyId == companyId, ct);
        return source == null ? null : ToDto(source);
    }

    public async Task<IngestionSourceDto> CreateAsync(string companyId, string datasetId, string? userId, SaveIngestionSourceRequest request, CancellationToken ct = default)
    {
        var source = new IngestionSource
        {
            CompanyId = companyId,
            DatasetId = datasetId,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow
        };
        ApplyRequest(source, request, isCreate: true);
        _db.IngestionSource.Add(source);
        await _db.SaveChangesAsync(ct);
        return ToDto(source);
    }

    public async Task<IngestionSourceDto?> UpdateAsync(string companyId, string id, SaveIngestionSourceRequest request, CancellationToken ct = default)
    {
        var source = await _db.IngestionSource.FirstOrDefaultAsync(s => s.Id == id && s.CompanyId == companyId, ct);
        if (source == null) return null;

        ApplyRequest(source, request, isCreate: false);
        source.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(source);
    }

    public async Task<IngestionSourceDto?> SetEnabledAsync(string companyId, string id, bool enabled, CancellationToken ct = default)
    {
        var source = await _db.IngestionSource.FirstOrDefaultAsync(s => s.Id == id && s.CompanyId == companyId, ct);
        if (source == null) return null;

        source.IsEnabled = enabled;
        source.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(source);
    }

    public async Task<bool> DeleteAsync(string companyId, string id, CancellationToken ct = default)
    {
        var source = await _db.IngestionSource.FirstOrDefaultAsync(s => s.Id == id && s.CompanyId == companyId, ct);
        if (source == null) return false;

        // No cascade deletes in this codebase — remove run history explicitly first.
        var runs = await _db.IngestionRun.Where(r => r.SourceId == id).ToListAsync(ct);
        _db.IngestionRun.RemoveRange(runs);
        _db.IngestionSource.Remove(source);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<IngestionRunDto>> GetRunsAsync(string companyId, string sourceId, int limit = 20, CancellationToken ct = default)
    {
        return await _db.IngestionRun
            .Where(r => r.SourceId == sourceId && r.CompanyId == companyId)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .Select(r => new IngestionRunDto
            {
                Id = r.Id,
                StartedAt = r.StartedAt,
                FinishedAt = r.FinishedAt,
                Status = r.Status,
                RowsIngested = r.RowsIngested,
                ErrorMessage = r.ErrorMessage,
                JobId = r.JobId,
                Log = r.Log
            })
            .ToListAsync(ct);
    }

    private void ApplyRequest(IngestionSource source, SaveIngestionSourceRequest request, bool isCreate)
    {
        source.TargetTable = request.TargetTable;
        source.Name = request.Name;
        source.Description = request.Description;
        source.SourceKind = request.SourceKind;
        source.SourceEntityId = request.SourceEntityId;
        source.SourceConfig = JsonSerializer.Serialize(request.Config ?? new IngestionSourceConfig());
        source.ImportMode = request.ImportMode;
        source.KeyColumns = request.KeyColumns is { Count: > 0 } ? string.Join(",", request.KeyColumns) : null;
        source.CreateIfNotExists = request.CreateIfNotExists;
        source.IncrementalColumn = string.IsNullOrWhiteSpace(request.IncrementalColumn) ? null : request.IncrementalColumn;
        source.CronExpression = request.CronExpression;
        source.TimeZone = request.TimeZone;
        source.IsEnabled = request.IsEnabled;

        // A non-null secret replaces the stored one; null leaves the existing secret untouched.
        if (request.Secret != null)
            source.SecretEncrypted = string.IsNullOrEmpty(request.Secret) ? null : _protector.Encrypt(request.Secret);
    }

    private static IngestionSourceConfig ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new IngestionSourceConfig();
        try { return JsonSerializer.Deserialize<IngestionSourceConfig>(json, JsonOpts) ?? new IngestionSourceConfig(); }
        catch { return new IngestionSourceConfig(); }
    }

    private static IngestionSourceDto ToDto(IngestionSource s) => new()
    {
        Id = s.Id,
        DatasetId = s.DatasetId,
        TargetTable = s.TargetTable,
        Name = s.Name,
        Description = s.Description,
        SourceKind = s.SourceKind,
        SourceEntityId = s.SourceEntityId,
        Config = ParseConfig(s.SourceConfig),
        HasSecret = !string.IsNullOrEmpty(s.SecretEncrypted),
        ImportMode = s.ImportMode,
        KeyColumns = string.IsNullOrWhiteSpace(s.KeyColumns)
            ? new List<string>()
            : s.KeyColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        CreateIfNotExists = s.CreateIfNotExists,
        IncrementalColumn = s.IncrementalColumn,
        IncrementalLastValue = s.IncrementalLastValue,
        CronExpression = s.CronExpression,
        TimeZone = s.TimeZone,
        IsEnabled = s.IsEnabled,
        LastRunAt = s.LastRunAt,
        LastRunStatus = s.LastRunStatus,
        LastRunMessage = s.LastRunMessage,
        LastRunRows = s.LastRunRows,
        CreatedAt = s.CreatedAt,
        CreatedBy = s.CreatedBy
    };

    // ---- Execution ----

    public async Task<ImportResult> RunSourceAsync(string sourceId, string? runId = null, string? jobId = null, IJobProgress? progress = null, CancellationToken ct = default)
    {
        // Capture every log line onto the run (for in-app viewing) while still forwarding to the
        // Hangfire console when running via the scheduler.
        var log = new RunLogProgress(progress);

        var source = await _db.IngestionSource.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null)
        {
            log.WriteLine("Ingestion source not found.");
            return new ImportResult { Error = "Ingestion source not found." };
        }

        // Reuse a pre-created (e.g. "Queued") run when a runId is supplied, so the batch flow's queued row
        // becomes this execution; otherwise start a fresh run record.
        IngestionRun? run = runId != null
            ? await _db.IngestionRun.FirstOrDefaultAsync(r => r.Id == runId, ct)
            : null;

        if (run == null)
        {
            run = new IngestionRun
            {
                SourceId = source.Id,
                CompanyId = source.CompanyId,
            };
            if (runId != null) run.Id = runId;
            _db.IngestionRun.Add(run);
        }

        run.StartedAt = DateTime.UtcNow;
        run.Status = "Running";
        if (!string.IsNullOrEmpty(jobId)) run.JobId = jobId;
        await _db.SaveChangesAsync(ct);

        log.WriteLine($"Ingestion '{source.Name}' → {source.DatasetId}/{source.TargetTable} (run {run.Id}).");
        log.SetProgress(5);

        string? tempPath = null;
        ImportResult result;
        try
        {
            var config = ParseConfig(source.SourceConfig);

            // Live row count from the source fetch (throttled by the reader) → log.
            var fetchProgress = new Progress<long>(rows =>
                log.WriteLine($"Fetched {rows:N0} rows from source…"));

            var mode = Enum.TryParse<ImportMode>(source.ImportMode, ignoreCase: true, out var m) ? m : ImportMode.Append;
            var keys = string.IsNullOrWhiteSpace(source.KeyColumns)
                ? new List<string>()
                : source.KeyColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            log.WriteLine($"Fetching from {source.SourceKind}…");

            // Preferred path for a batched ClickHouse source: import each fetched page straight into the dataset,
            // so we never buffer the whole result. Returns null when it doesn't apply (then fall back to the
            // fetch-to-temp-file-then-import path used by all other sources / non-batched fetches).
            result = await TryRunBatchedDatabaseImportAsync(source, config, mode, keys, log, ct)
                     ?? await FetchToTempThenImportAsync(source, config, mode, keys, fetchProgress, log, p => tempPath = p, ct);

            progress?.SetProgress(90);

            // Advance the watermark to the highest value now present in the target table.
            if (result.Success && !string.IsNullOrWhiteSpace(source.IncrementalColumn))
            {
                var newWatermark = await ReadMaxAsync(source.DatasetId, source.TargetTable, source.IncrementalColumn!, ct);
                if (newWatermark != null)
                    source.IncrementalLastValue = newWatermark;
            }

            run.Status = result.Success ? "Success" : "Failed";
            run.ErrorMessage = result.Error;
            run.RowsIngested = result.RowsInserted + result.RowsUpdated;

            if (result.Success)
                log.WriteLine($"Done: {result.RowsInserted:N0} inserted, {result.RowsUpdated:N0} updated, {result.RowsSkipped:N0} skipped.");
            else
                log.WriteLine($"Failed: {result.Error}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingestion source {SourceId} failed.", sourceId);
            log.WriteLine($"Error: {ex.Message}");
            result = new ImportResult { Error = ex.Message };
            run.Status = "Failed";
            run.ErrorMessage = ex.Message;
        }
        finally
        {
            run.FinishedAt = DateTime.UtcNow;
            run.Log = log.Text;
            source.LastRunAt = run.FinishedAt;
            source.LastRunStatus = run.Status;
            source.LastRunMessage = run.ErrorMessage;
            source.LastRunRows = run.RowsIngested;
            await _db.SaveChangesAsync(ct);
            TryDelete(tempPath);
            log.SetProgress(100);
        }

        return result;
    }

    /// <summary>Forwards progress to the (optional) Hangfire console while capturing all lines as text,
    /// so the run's processing log can be persisted and viewed in-app.</summary>
    private sealed class RunLogProgress : IJobProgress
    {
        private readonly IJobProgress? _inner;
        private readonly System.Text.StringBuilder _sb = new();

        public RunLogProgress(IJobProgress? inner) => _inner = inner;

        public void WriteLine(string message)
        {
            _sb.Append(DateTime.UtcNow.ToString("HH:mm:ss")).Append("  ").AppendLine(message);
            _inner?.WriteLine(message);
        }

        public void SetProgress(int percent) => _inner?.SetProgress(percent);

        public string Text => _sb.ToString();
    }

    /// <summary>Fallback ingestion path: fetch the whole source result into a temp file, then import it in one
    /// call. <paramref name="setTempPath"/> hands the temp path back to the caller so it can be cleaned up.</summary>
    private async Task<ImportResult> FetchToTempThenImportAsync(IngestionSource source, IngestionSourceConfig config, ImportMode mode, List<string> keys, IProgress<long>? fetchProgress, RunLogProgress log, Action<string> setTempPath, CancellationToken ct)
    {
        var (tempPath, format) = await FetchToTempAsync(source, config, fetchProgress, log, ct);
        setTempPath(tempPath);
        log.WriteLine("Fetch complete; loading into the dataset…");
        log.SetProgress(60);

        await using var stream = File.OpenRead(tempPath);
        return await _duckdb.ImportFileAsync(source.DatasetId, source.TargetTable, stream, format, mode, keys,
            skipInvalidRows: true, createIfMissing: source.CreateIfNotExists, ct);
    }

    /// <summary>Per-batch streaming import for a batched ClickHouse ExternalDatabase source: pages the query and
    /// imports each page into the dataset as it arrives, so the whole result is never buffered. Returns null when
    /// it doesn't apply (wrong source kind / engine, or no batch size + key configured) so the caller falls back
    /// to <see cref="FetchToTempThenImportAsync"/>.</summary>
    private async Task<ImportResult?> TryRunBatchedDatabaseImportAsync(IngestionSource source, IngestionSourceConfig config, ImportMode mode, List<string> keys, RunLogProgress log, CancellationToken ct)
    {
        var kind = Enum.TryParse<IngestionSourceKind>(source.SourceKind, ignoreCase: true, out var k) ? k : IngestionSourceKind.ExternalDatabase;
        if (kind != IngestionSourceKind.ExternalDatabase) return null;
        if (!(config.BatchSize is int batchSize && batchSize > 0)) return null;

        var batchKey = !string.IsNullOrWhiteSpace(config.BatchKeyColumn) ? config.BatchKeyColumn : source.IncrementalColumn;
        if (string.IsNullOrWhiteSpace(batchKey) || string.IsNullOrWhiteSpace(source.SourceEntityId)) return null;

        var conn = await _dbTables.GetDecryptedConnectionAsync(source.SourceEntityId!, source.CompanyId, ct);
        // Per-batch import is wired for ClickHouse (paged over the HTTP CSV endpoint); other engines fall back.
        if (conn == null || conn.DatabaseType != Application.Shared.Enums.DataSourceType.ClickHouse) return null;

        // Build the effective query (same as the single-shot database fetch, including the incremental watermark).
        var baseQuery = !string.IsNullOrWhiteSpace(config.Query)
            ? config.Query!.TrimEnd().TrimEnd(';')
            : $"SELECT * FROM {QualifyTable(config.Schema, config.Table)}";
        var query = baseQuery;
        if (!string.IsNullOrWhiteSpace(source.IncrementalColumn) && !string.IsNullOrWhiteSpace(source.IncrementalLastValue))
            query = $"SELECT * FROM ({baseQuery}) _src WHERE _src.{source.IncrementalColumn} > {IncrementalLiteral(source.IncrementalLastValue!)}";

        log.WriteLine("Executing query against the source database (importing each batch as it is fetched):");
        log.WriteLine(query);
        log.WriteLine($"Batch size {batchSize:N0} rows, ordered by '{batchKey}'.");
        log.SetProgress(15);

        var agg = new ImportResult { Success = true };
        var pageIndex = 0;
        var runningRows = 0;
        var enc = new UTF8Encoding(false);

        await _dbTables.ReadClickHouseBatchesAsync(conn, query, batchKey!, batchSize, async (pageCsv, pageRows) =>
        {
            // First batch honors the configured mode (e.g. Replace clears the table first); later batches append
            // so they don't wipe the rows already loaded this run. Append/Upsert apply uniformly to every page.
            var pageMode = pageIndex == 0 ? mode : (mode == ImportMode.Replace ? ImportMode.Append : mode);

            using var stream = new MemoryStream(enc.GetBytes(pageCsv));
            var r = await _duckdb.ImportFileAsync(source.DatasetId, source.TargetTable, stream, ImportFileFormat.Csv,
                pageMode, keys, skipInvalidRows: true, createIfMissing: source.CreateIfNotExists, ct);

            if (!r.Success)
            {
                agg.Success = false;
                agg.Error = r.Error;
                throw new InvalidOperationException(r.Error ?? "Batch import failed.");
            }

            agg.RowsInserted += r.RowsInserted;
            agg.RowsUpdated += r.RowsUpdated;
            agg.RowsSkipped += r.RowsSkipped;

            pageIndex++;
            runningRows += pageRows;
            log.WriteLine($"Batch {pageIndex}: imported {pageRows:N0} rows (running total {runningRows:N0}).");
        }, ct, config.CommandTimeoutSeconds);

        if (pageIndex == 0)
            log.WriteLine("No rows returned from the source.");

        return agg;
    }

    public async Task<string> CreateQueuedRunAsync(string companyId, string sourceId, CancellationToken ct = default)
    {
        var run = new IngestionRun
        {
            SourceId = sourceId,
            CompanyId = companyId,
            StartedAt = DateTime.UtcNow,
            Status = "Queued"
        };
        _db.IngestionRun.Add(run);
        await _db.SaveChangesAsync(ct);
        return run.Id;
    }

    public async Task SetRunJobIdAsync(string runId, string jobId, CancellationToken ct = default)
    {
        // Targeted column update so it can't clobber a Status the executing job may have already written.
        await _db.IngestionRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, jobId), ct);
    }

    public async Task<int> FailStaleRunsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        // A threshold (rather than "every Running row") is deliberate: the web app and scheduler share
        // this table, so a freshly-started run owned by the *other* process must not be failed here.
        var cutoff = DateTime.UtcNow - olderThan;
        var stale = await _db.IngestionRun
            .Where(r => r.Status == "Running" && r.StartedAt < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        const string message = "Marked as failed: the run did not finish (the process was restarted or it exceeded the stale-run timeout).";

        foreach (var run in stale)
        {
            run.Status = "Failed";
            run.FinishedAt = now;
            run.ErrorMessage = message;
        }

        // Reflect the failure on each parent source when the orphaned run is its most recent activity,
        // so the source's status pill doesn't keep showing the stale outcome.
        foreach (var group in stale.GroupBy(r => r.SourceId))
        {
            var latest = group.OrderByDescending(r => r.StartedAt).First();
            var source = await _db.IngestionSource.FirstOrDefaultAsync(s => s.Id == group.Key, ct);
            if (source != null && (source.LastRunAt == null || source.LastRunAt <= latest.StartedAt))
            {
                source.LastRunAt = now;
                source.LastRunStatus = "Failed";
                source.LastRunMessage = message;
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Reconciled {Count} stale ingestion run(s) started before {Cutoff:o}.", stale.Count, cutoff);
        return stale.Count;
    }

    public async Task<int> FailRunningRunsForSourceAsync(string companyId, string sourceId, CancellationToken ct = default)
    {
        var running = await _db.IngestionRun
            .Where(r => r.SourceId == sourceId && r.CompanyId == companyId && r.Status == "Running")
            .ToListAsync(ct);
        if (running.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        const string message = "Marked as failed manually — the run was stuck in Running.";

        foreach (var run in running)
        {
            run.Status = "Failed";
            run.FinishedAt = now;
            run.ErrorMessage = message;
        }

        var source = await _db.IngestionSource.FirstOrDefaultAsync(s => s.Id == sourceId && s.CompanyId == companyId, ct);
        if (source != null)
        {
            source.LastRunAt = now;
            source.LastRunStatus = "Failed";
            source.LastRunMessage = message;
        }

        await _db.SaveChangesAsync(ct);
        return running.Count;
    }

    public async Task<ImportResult> SnapshotQueryAsync(string companyId, string datasetId, string sourceEntityId, string sql, string targetTable, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceEntityId))
            return new ImportResult { Error = "This dataset has no linked database entity." };
        if (string.IsNullOrWhiteSpace(targetTable))
            return new ImportResult { Error = "A target table name is required." };

        var conn = await _dbTables.GetDecryptedConnectionAsync(sourceEntityId, companyId, ct);
        if (conn == null)
            return new ImportResult { Error = "No connection is configured on the source database entity." };

        var tempPath = TempPath(".csv");
        try
        {
            await _dbTables.ReadToTempCsvAsync(conn, sql.TrimEnd().TrimEnd(';'), tempPath, ct);
            await using var stream = File.OpenRead(tempPath);
            // Replace + create-if-missing so re-snapshotting refreshes the table in place.
            return await _duckdb.ImportFileAsync(datasetId, targetTable, stream, ImportFileFormat.Csv,
                ImportMode.Replace, new List<string>(), skipInvalidRows: true, createIfMissing: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot of external query into {Dataset}/{Table} failed.", datasetId, targetTable);
            return new ImportResult { Error = ex.Message };
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    // Fetches the source's data to a temp file and reports its format. rowProgress (optional) receives a
    // running row count from the DB fetch.
    private async Task<(string path, ImportFileFormat format)> FetchToTempAsync(IngestionSource source, IngestionSourceConfig config, IProgress<long>? rowProgress, IJobProgress? log, CancellationToken ct)
    {
        var kind = Enum.TryParse<IngestionSourceKind>(source.SourceKind, ignoreCase: true, out var k) ? k : IngestionSourceKind.ExternalDatabase;
        return kind switch
        {
            IngestionSourceKind.ExternalDatabase => await FetchFromDatabaseAsync(source, config, rowProgress, log, ct),
            IngestionSourceKind.Rest => await FetchFromRestAsync(source, config, ct),
            IngestionSourceKind.Blob => await FetchFromBlobAsync(source, config, ct),
            IngestionSourceKind.Sftp => await FetchFromSftpAsync(source, config, ct),
            _ => throw new NotSupportedException($"Unsupported ingestion source kind: {source.SourceKind}")
        };
    }

    private async Task<(string, ImportFileFormat)> FetchFromDatabaseAsync(IngestionSource source, IngestionSourceConfig config, IProgress<long>? rowProgress, IJobProgress? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.SourceEntityId))
            throw new InvalidOperationException("This database source has no linked database entity.");

        var conn = await _dbTables.GetDecryptedConnectionAsync(source.SourceEntityId!, source.CompanyId, ct);
        if (conn == null)
            throw new InvalidOperationException("No connection is configured on the source database entity.");

        var baseQuery = !string.IsNullOrWhiteSpace(config.Query)
            ? config.Query!.TrimEnd().TrimEnd(';')
            : $"SELECT * FROM {QualifyTable(config.Schema, config.Table)}";

        // Incremental: only pull rows newer than the last watermark.
        var query = baseQuery;
        if (!string.IsNullOrWhiteSpace(source.IncrementalColumn) && !string.IsNullOrWhiteSpace(source.IncrementalLastValue))
            query = $"SELECT * FROM ({baseQuery}) _src WHERE _src.{source.IncrementalColumn} > {IncrementalLiteral(source.IncrementalLastValue!)}";

        // Surface the exact SQL that's about to run, so the run log shows the query before the (often long) fetch.
        log?.WriteLine("Executing query against the source database:");
        log?.WriteLine(query);

        var path = TempPath(".csv");

        // Optional keyset batching for very large tables: read in ordered pages so no single query has to
        // return millions of rows at once. Falls back to a single streaming read otherwise. Either way a
        // generous (configurable) command timeout is applied so long pulls don't time out.
        var batchKey = !string.IsNullOrWhiteSpace(config.BatchKeyColumn) ? config.BatchKeyColumn : source.IncrementalColumn;
        if (config.BatchSize is int bs && bs > 0 && !string.IsNullOrWhiteSpace(batchKey))
            log?.WriteLine($"Batching the fetch in pages of {bs:N0} rows, ordered by '{batchKey}'.");
        else if (config.BatchSize is int bs2 && bs2 > 0)
            log?.WriteLine($"Batch size {bs2:N0} is set but no batch key column is configured — a batch key is required to page, so streaming the full result in a single request.");
        else
            log?.WriteLine("Streaming the full result in a single request (no batch size configured).");

        if (config.BatchSize is int batchSize && batchSize > 0 && !string.IsNullOrWhiteSpace(batchKey))
            await _dbTables.ReadToTempCsvBatchedAsync(conn, query, batchKey!, batchSize, path, ct, config.CommandTimeoutSeconds, rowProgress);
        else
            await _dbTables.ReadToTempCsvAsync(conn, query, path, ct, config.CommandTimeoutSeconds, rowProgress);

        return (path, ImportFileFormat.Csv);
    }

    private async Task<(string, ImportFileFormat)> FetchFromRestAsync(IngestionSource source, IngestionSourceConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
            throw new InvalidOperationException("REST source has no URL configured.");

        var client = _httpClientFactory.CreateClient();
        var method = string.IsNullOrWhiteSpace(config.Method) ? HttpMethod.Get : new HttpMethod(config.Method!.ToUpperInvariant());
        using var request = new HttpRequestMessage(method, config.Url);

        if (config.Headers != null)
            foreach (var (key, value) in config.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

        var secret = DecryptSecret(source);
        switch ((config.AuthType ?? "none").ToLowerInvariant())
        {
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;
            case "basic":
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{secret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                break;
        }

        if (!string.IsNullOrEmpty(config.Body) && method != HttpMethod.Get)
            request.Content = new StringContent(config.Body!, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"REST request failed ({(int)response.StatusCode}): {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var element = NavigateJsonPath(doc.RootElement, config.JsonPath);

        var path = TempPath(".json");
        var rawJson = element.ValueKind == JsonValueKind.Array
            ? element.GetRawText()
            : "[" + element.GetRawText() + "]";
        await File.WriteAllTextAsync(path, rawJson, new UTF8Encoding(false), ct);
        return (path, ImportFileFormat.Json);
    }

    private async Task<(string, ImportFileFormat)> FetchFromBlobAsync(IngestionSource source, IngestionSourceConfig config, CancellationToken ct)
    {
        var connectionString = DecryptSecret(source);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Blob source has no connection string/SAS configured (set the secret).");
        if (string.IsNullOrWhiteSpace(config.Container) || string.IsNullOrWhiteSpace(config.BlobPath))
            throw new InvalidOperationException("Blob source requires a container and blob path.");

        var container = new BlobContainerClient(connectionString, config.Container);
        var blob = container.GetBlobClient(config.BlobPath);

        var format = ResolveFileFormat(config.FileFormat, config.BlobPath!);
        var path = TempPath(ExtFor(format));
        await blob.DownloadToAsync(path, ct);
        return (path, format);
    }

    private async Task<(string, ImportFileFormat)> FetchFromSftpAsync(IngestionSource source, IngestionSourceConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.RemotePath))
            throw new InvalidOperationException("SFTP source requires a host and remote path.");

        var secret = DecryptSecret(source);
        var format = ResolveFileFormat(config.FileFormat, config.RemotePath!);
        var path = TempPath(ExtFor(format));

        // SSH.NET is synchronous; run off the request/scheduler thread.
        await Task.Run(() =>
        {
            using var sftp = new SftpClient(config.Host, config.Port ?? 22, config.SftpUsername ?? "", secret ?? "");
            sftp.Connect();
            try
            {
                using var fs = File.Create(path);
                sftp.DownloadFile(config.RemotePath, fs);
            }
            finally
            {
                sftp.Disconnect();
            }
        }, ct);

        return (path, format);
    }

    // Highest value of the watermark column now in the target table (as a string for storage).
    private async Task<string?> ReadMaxAsync(string datasetId, string tableName, string column, CancellationToken ct)
    {
        var sql = $"SELECT MAX(\"{column.Replace("\"", "\"\"")}\") AS m FROM \"{tableName.Replace("\"", "\"\"")}\"";
        var r = await _duckdb.ExecuteSqlAsync(datasetId, sql, allowWrite: false, maxRows: 1, ct);
        if (!string.IsNullOrEmpty(r.Error) || r.Rows.Count == 0) return null;

        var value = r.Rows[0].Values.FirstOrDefault();
        return value switch
        {
            null => null,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private string? DecryptSecret(IngestionSource source) =>
        string.IsNullOrEmpty(source.SecretEncrypted) ? null : _protector.Decrypt(source.SecretEncrypted);

    private static string QualifyTable(string? schema, string? table)
    {
        if (string.IsNullOrWhiteSpace(table))
            throw new InvalidOperationException("Database source requires a query or a table name.");
        return string.IsNullOrWhiteSpace(schema) ? table! : $"{schema}.{table}";
    }

    // Numeric watermarks are emitted unquoted; everything else (timestamps, ids as strings) is quoted.
    private static string IncrementalLiteral(string lastValue)
    {
        if (long.TryParse(lastValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            double.TryParse(lastValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return lastValue;
        return "'" + lastValue.Replace("'", "''") + "'";
    }

    private static JsonElement NavigateJsonPath(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next))
                current = next;
            else
                throw new InvalidOperationException($"JSON path segment '{segment}' not found in the response.");
        }
        return current;
    }

    private static ImportFileFormat ResolveFileFormat(string? explicitFormat, string path)
    {
        if (!string.IsNullOrWhiteSpace(explicitFormat) && Enum.TryParse<ImportFileFormat>(explicitFormat, ignoreCase: true, out var fmt))
            return fmt;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tsv" => ImportFileFormat.Tsv,
            ".json" => ImportFileFormat.Json,
            ".parquet" => ImportFileFormat.Parquet,
            ".xlsx" => ImportFileFormat.Excel,
            _ => ImportFileFormat.Csv
        };
    }

    private static string ExtFor(ImportFileFormat format) => format switch
    {
        ImportFileFormat.Tsv => ".tsv",
        ImportFileFormat.Json => ".json",
        ImportFileFormat.Parquet => ".parquet",
        ImportFileFormat.Excel => ".xlsx",
        _ => ".csv"
    };

    private static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), $"ingest_{Guid.NewGuid()}{ext}");

    private static void TryDelete(string? path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    private static string Truncate(string s, int max = 500) => s.Length <= max ? s : s.Substring(0, max);
}

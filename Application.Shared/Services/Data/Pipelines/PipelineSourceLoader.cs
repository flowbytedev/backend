using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Gets a source step's data into the run's scratch database. Everything downstream is SQL, so this is the
/// only place in the feature that talks to anything other than DuckDB.
/// <para>
/// The three source kinds differ only in how the bytes arrive:
/// </para>
/// <list type="bullet">
/// <item><b>File</b> — DuckDB reads it directly, which is why Excel and Parquet cost nothing extra.</item>
/// <item><b>Dataset</b> — a local dataset is a brief cross-file ATTACH; an external one is really a
/// database source wearing a dataset's name, so it is routed as one.</item>
/// <item><b>Database</b> — streamed to a temp CSV using the reader the scheduled-ingestion path already
/// uses, then loaded. The CSV hop is not free (see the type note on <see cref="LoadDatabaseAsync"/>) but it
/// is the mechanism this codebase already trusts for millions of rows.</item>
/// </list>
/// </summary>
public interface IPipelineSourceLoader
{
    Task<PipelineRelationResult> LoadAsync(SourceLoadRequest request, CancellationToken ct = default);
}

/// <summary>What to load, and where to put it.</summary>
public sealed class SourceLoadRequest
{
    public required PipelineNodeDef Node { get; init; }
    public required string CompanyId { get; init; }

    /// <summary>The run's scratch dataset — where the relation is created.</summary>
    public required string ScratchDatasetId { get; init; }

    /// <summary>Relation name to create, which is always the node's id.</summary>
    public required string Relation { get; init; }

    /// <summary>Resolves <c>{{ run.* }}</c> tokens in paths and queries.</summary>
    public required Func<string?, string> ResolveTokens { get; init; }

    /// <summary>Set during a preview, so a source is sampled rather than fully read.</summary>
    public int? RowLimit { get; init; }

    /// <summary>A file attached to a manual run, for an upload source.</summary>
    public string? UploadedFilePath { get; init; }

    public IJobProgress? Progress { get; init; }

    /// <summary>
    /// Last committed watermark for this step, or null on a first run / after a reset. Supplied by the
    /// engine, which owns the state table.
    /// </summary>
    public string? IncrementalLow { get; init; }

    /// <summary>
    /// Called once the loader has captured this run's window. The engine holds it and commits the ceiling
    /// only if the whole run succeeds — committing here would advance the mark for a run that then failed,
    /// and the skipped rows would never be read again.
    /// </summary>
    public Action<PipelineWatermarkWindow>? OnWindowCaptured { get; init; }
}

public class PipelineSourceLoader(
    ApplicationDbContext db,
    IPipelineStore store,
    IDatabaseTableService databaseTables,
    IPipelineFileResolver files,
    IPipelineApiClient apiClient,
    IPipelineApiReader apiReader,
    PipelineOptions options) : IPipelineSourceLoader
{
    public async Task<PipelineRelationResult> LoadAsync(SourceLoadRequest request, CancellationToken ct = default)
    {
        return request.Node.Type switch
        {
            PipelineNodeTypes.SourceFile => await LoadFileAsync(request, ct),
            PipelineNodeTypes.SourceDataset => await LoadDatasetAsync(request, ct),
            PipelineNodeTypes.SourceDatabase => await LoadDatabaseAsync(request, null, ct),
            PipelineNodeTypes.SourceApi => await LoadApiAsync(request, ct),
            _ => PipelineRelationResult.Fail(
                $"'{request.Node.Type}' is not a source step.", PipelineErrorType.Invalid)
        };
    }

    // ------------------------------------------------------------------- api

    /// <summary>
    /// Fetches every page into a JSON file, then hands that to the same DuckDB reader a file source uses.
    /// Going through a file rather than building a relation row by row means an API source gets the type
    /// inference, the row cap and the error handling that the file path already has.
    /// </summary>
    private async Task<PipelineRelationResult> LoadApiAsync(SourceLoadRequest request, CancellationToken ct)
    {
        var config = request.Node.Config;

        var reference = request.ResolveTokens(Str(config, "credential"));
        if (string.IsNullOrWhiteSpace(reference))
            return PipelineRelationResult.Fail(
                "This API step has no credential.", PipelineErrorType.Invalid);

        var credential = await apiClient.ResolveAsync(reference, request.CompanyId, forWrite: false, ct);
        if (credential is null)
            return PipelineRelationResult.Fail(
                $"No API credential called '{reference}' is available to this company.",
                PipelineErrorType.Invalid);

        if (!credential.Credential.IsEnabled)
            return PipelineRelationResult.Fail(
                $"The API credential '{credential.Name}' is disabled.",
                PipelineErrorType.SourceUnavailable);

        var pagination = Str(config, "pagination") ?? PipelineApiPagination.None;

        var fetch = await apiReader.FetchToFileAsync(new ApiFetchRequest
        {
            Credential = credential,
            Url = request.ResolveTokens(Str(config, "url")) ?? string.Empty,
            Method = Str(config, "method") ?? "GET",
            Headers = Headers(config, "headers", request.ResolveTokens),
            Body = request.ResolveTokens(Str(config, "body")),
            JsonPath = Str(config, "jsonPath"),

            Pagination = pagination,
            PageParam = Str(config, pagination == PipelineApiPagination.Offset ? "offsetParam" : "pageParam"),
            PageSizeParam = Str(config, "pageSizeParam"),
            PageSize = Int(config, "pageSize") ?? options.ResolveApiPageSize(),
            StartPage = Int(config, "startPage") ?? 1,
            CursorPath = Str(config, "cursorPath"),
            CursorParam = Str(config, "cursorParam"),

            Flatten = Str(config, "flatten") ?? PipelineApiFlatten.OneLevel,

            // A preview must not walk a thousand pages to show twenty rows.
            RowLimit = request.RowLimit,
            MaxPages = request.RowLimit is not null ? 1 : options.ResolveApiMaxPages(),

            Progress = request.Progress
        }, ct);

        if (!fetch.Success)
            return PipelineRelationResult.Fail(fetch.Error!, fetch.ErrorType!);

        request.Progress?.WriteLine(
            $"      {fetch.RowCount:N0} record(s) over {fetch.Pages} request(s), "
            + $"{fetch.Columns.Count} column(s)");

        try
        {
            return await store.MaterializeFromFileAsync(
                request.ScratchDatasetId, request.Relation, fetch.FilePath!, ImportFileFormat.Json,
                hasHeader: true, sheet: null, rowLimit: request.RowLimit, ct: ct);
        }
        finally
        {
            TryDelete(fetch.FilePath);
        }
    }

    /// <summary>
    /// Reads a keyvalue field into a header dictionary, resolving tokens in the values so a header can carry
    /// {{ run.date }}.
    /// </summary>
    private static Dictionary<string, string>? Headers(
        JsonObject? config, string key, Func<string?, string> resolveTokens)
    {
        if (config?[key] is not JsonObject obj || obj.Count == 0) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in obj)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            headers[name] = resolveTokens(value?.ToString()) ?? string.Empty;
        }

        return headers.Count == 0 ? null : headers;
    }

    // ------------------------------------------------------------------- file

    private async Task<PipelineRelationResult> LoadFileAsync(SourceLoadRequest request, CancellationToken ct)
    {
        var config = request.Node.Config;
        var location = Str(config, "location") ?? PipelineFileLocations.Folder;

        var resolved = await files.ResolveAsync(new PipelineFileRequest
        {
            Location = location,
            Path = request.ResolveTokens(Str(config, "path")),
            Pick = Str(config, "pick"),
            Container = request.ResolveTokens(Str(config, "container")),
            BlobPath = request.ResolveTokens(Str(config, "blobPath")),
            UploadedPath = request.UploadedFilePath
        }, ct);

        if (!resolved.Success)
            return PipelineRelationResult.Fail(resolved.Error!, resolved.ErrorType!);

        request.Progress?.WriteLine($"  reading {resolved.OriginalPath}");

        var format = ParseFormat(Str(config, "format"));
        var hasHeader = config?["hasHeader"] is not JsonValue h || !h.TryGetValue<bool>(out var flag) || flag;

        var badRowMode = Str(config, "onBadRow") ?? PipelineBadRowModes.Fail;

        try
        {
            var loaded = await store.MaterializeFromFileAsync(
                request.ScratchDatasetId, request.Relation, resolved.LocalPath!, format,
                hasHeader, Str(config, "sheet"), rowLimit: request.RowLimit,
                readOptions: PipelineFileReadOptions.FromConfig(config, v => request.ResolveTokens(v)),
                addSourceFileColumn: config?["addSourceFile"] is JsonValue lineage
                                     && lineage.TryGetValue<bool>(out var wantsFile) && wantsFile,
                badRowMode: badRowMode,
                ct: ct);

            if (loaded.Success && loaded.RowsRejected > 0)
            {
                var cap = Int(config, "maxBadRows");

                // Checked after the read rather than during it: DuckDB has no "stop after n rejects", and
                // reading the file twice to find out would cost more than reading it once and refusing.
                if (cap is int limit && limit >= 0 && loaded.RowsRejected > limit)
                {
                    return PipelineRelationResult.Fail(
                        $"{loaded.RowsRejected:N0} row(s) could not be read, which is more than the "
                        + $"{limit:N0} allowed. Nothing was loaded from this step.",
                        PipelineErrorType.BadRows, loaded.Sql);
                }

                request.Progress?.WriteLine(
                    $"      {loaded.RowsRejected:N0} row(s) skipped"
                    + (loaded.RejectRelation is null
                        ? " (not kept — set this step to quarantine to keep them)"
                        : $" and kept in {loaded.RejectRelation}"));
            }

            return loaded;
        }
        finally
        {
            // A blob download is this run's own copy; an uploaded file is cleaned up by the run itself
            // after every step that might reference it, so it is left alone here.
            if (resolved.IsTemporary && location == PipelineFileLocations.Blob)
                TryDelete(resolved.LocalPath);
        }
    }

    // ---------------------------------------------------------------- dataset

    private async Task<PipelineRelationResult> LoadDatasetAsync(SourceLoadRequest request, CancellationToken ct)
    {
        var config = request.Node.Config;
        var reference = Str(config, "dataset");
        var table = Str(config, "table");

        if (string.IsNullOrWhiteSpace(reference))
            return PipelineRelationResult.Fail("This step has no dataset.", PipelineErrorType.Invalid);
        if (string.IsNullOrWhiteSpace(table))
            return PipelineRelationResult.Fail("This step has no table.", PipelineErrorType.Invalid);

        var dataset = await FindDatasetAsync(reference!, request.CompanyId, ct);
        if (dataset is null)
            return PipelineRelationResult.Fail(
                $"No dataset called '{reference}' is available to this company.",
                PipelineErrorType.SourceUnavailable);

        var columns = StringList(config, "columns");
        var where = request.ResolveTokens(Str(config, "where"));

        // Incremental window, ANDed with whatever filter the step already had.
        var incremental = PipelineIncrementalConfig.FromConfig(config, v => request.ResolveTokens(v));
        if (incremental.IsEnabled)
        {
            var window = await CaptureWindowAsync(request, incremental, dataset, table!, ct);
            if (window is null)
                return PipelineRelationResult.Fail(
                    $"Could not read the high-water mark for '{incremental.Column}'. "
                    + "Check the column exists on the source.",
                    PipelineErrorType.SourceUnavailable);

            request.OnWindowCaptured?.Invoke(window);
            request.Progress?.WriteLine($"  incremental: {window.Describe()}");

            var predicate = window.ToPredicate();
            if (predicate is not null)
                where = string.IsNullOrWhiteSpace(where) ? predicate : $"({where}) AND ({predicate})";
        }

        // An external dataset has no DuckDB tables of its own to attach — it is a live view over a database
        // connection. So it is loaded as a database source, using the connection the dataset points at.
        if (dataset.SourceType == DatasetSourceType.External)
        {
            if (string.IsNullOrWhiteSpace(dataset.SourceEntityId))
                return PipelineRelationResult.Fail(
                    $"Dataset '{dataset.Name}' is external but has no database connection configured.",
                    PipelineErrorType.SourceUnavailable);

            var projection = columns.Count > 0
                ? string.Join(", ", columns.Select(PipelineSql.Q))
                : "*";
            var filter = string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE ({where})";
            var query = $"SELECT {projection} FROM {PipelineSql.Q(table!)}{filter}";

            request.Progress?.WriteLine(
                $"  reading {table} from external dataset '{dataset.Name}'");

            return await LoadDatabaseAsync(request, new DatabaseFetchSpec
            {
                EntityId = dataset.SourceEntityId!,
                Query = query
            }, ct);
        }

        request.Progress?.WriteLine($"  reading {table} from dataset '{dataset.Name}'");

        return await store.MaterializeFromDatasetAsync(
            request.ScratchDatasetId, request.Relation, dataset.Id!, table!,
            columns.Count > 0 ? columns : null, where, rowLimit: request.RowLimit, ct: ct);
    }

    /// <summary>
    /// The source database's current maximum for the watermark column.
    /// <para>
    /// Wraps the step's own query so the ceiling is measured over exactly the rows the step would read. A
    /// <c>MAX</c> over the bare table would be wrong whenever the query filters — the ceiling would jump
    /// past rows the step never saw, and they would be skipped for good.
    /// </para>
    /// <para>
    /// Distinguishes "read failed" from "read returned nothing": an empty result is a legitimate null
    /// ceiling, while a failure must stop the run rather than be treated as an empty source, which would
    /// silently load zero rows and report success.
    /// </para>
    /// </summary>
    private async Task<(bool Failed, string? Value, string? Error)> ReadDatabaseCeilingAsync(
        string entityId, string companyId, string query, string column, CancellationToken ct)
    {
        var sql = $"SELECT MAX({PipelineSql.Q(column)}) AS m FROM ({query}) _wm";

        var result = await databaseTables.ExecuteQueryAsync(entityId, companyId, sql, maxRows: 1, ct);

        if (!string.IsNullOrEmpty(result.Error)) return (true, null, result.Error);
        if (result.Rows.Count == 0) return (false, null, null);

        return (false, PipelineWatermarkWindow.Portable(result.Rows[0].Values.FirstOrDefault()), null);
    }

    /// <summary>
    /// Reads the source's current maximum for the watermark column — this run's ceiling.
    /// <para>
    /// Read from the SOURCE before loading, not from the destination afterwards. Scheduled ingestion does
    /// the latter, which skips rows sharing the boundary value and misses anything written while the load
    /// was in flight. Returns null only when the read itself failed; an empty source is a successful read
    /// of nothing and comes back as a window with a null ceiling.
    /// </para>
    /// </summary>
    private async Task<PipelineWatermarkWindow?> CaptureWindowAsync(
        SourceLoadRequest request, PipelineIncrementalConfig incremental,
        Dataset dataset, string table, CancellationToken ct)
    {
        var column = incremental.Column!;
        var sql = $"SELECT MAX({PipelineSql.Q(column)}) FROM {PipelineSql.Q(table)}";

        string? high;
        string? type;

        if (dataset.SourceType == DatasetSourceType.External)
        {
            if (string.IsNullOrWhiteSpace(dataset.SourceEntityId)) return null;

            var result = await databaseTables.ExecuteQueryAsync(
                dataset.SourceEntityId!, request.CompanyId, sql, maxRows: 1, ct);

            if (!string.IsNullOrEmpty(result.Error) || result.Rows.Count == 0) return null;

            high = PipelineWatermarkWindow.Portable(result.Rows[0].Values.FirstOrDefault());
            type = null;
        }
        else
        {
            var result = await store.ReadScalarAsync(dataset.Id!, sql, ct: ct);
            if (!result.Success) return null;

            high = PipelineWatermarkWindow.Portable(result.Value);
            type = result.TypeName;
        }

        return new PipelineWatermarkWindow
        {
            Column = column,
            // A configured start acts as the low bound until the first run commits one, so a table with ten
            // years of history can begin from last month rather than the beginning.
            Low = request.IncrementalLow ?? incremental.Start,
            High = high,
            Type = type
        };
    }

    // --------------------------------------------------------------- database

    /// <summary>
    /// Reads from a registered database connection.
    /// <para>
    /// <b>The CSV hop loses type information.</b> Rows go out as text and come back through
    /// <c>read_csv_auto</c>, which re-infers types from content — so a leading-zero item code can arrive as
    /// a number. That is the price of reusing the streaming reader that scheduled ingestion already relies
    /// on for large tables, and it is survivable because the destination write casts to the target table's
    /// real types. Where it matters mid-pipeline, a Map columns step with an explicit cast is the fix.
    /// </para>
    /// </summary>
    private async Task<PipelineRelationResult> LoadDatabaseAsync(
        SourceLoadRequest request, DatabaseFetchSpec? preresolved, CancellationToken ct)
    {
        var config = request.Node.Config;
        DatabaseFetchSpec spec;

        if (preresolved is not null)
        {
            spec = preresolved;
        }
        else
        {
            var reference = Str(config, "connection");
            if (string.IsNullOrWhiteSpace(reference))
                return PipelineRelationResult.Fail("This step has no connection.", PipelineErrorType.Invalid);

            var entityId = await ResolveEntityIdAsync(reference!, request.CompanyId, ct);
            if (entityId is null)
                return PipelineRelationResult.Fail(
                    $"No database connection called '{reference}' is available to this company.",
                    PipelineErrorType.SourceUnavailable);

            var mode = Str(config, "mode") ?? "table";
            string query;

            if (mode == "query")
            {
                query = request.ResolveTokens(Str(config, "query")) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(query))
                    return PipelineRelationResult.Fail("This step has no query.", PipelineErrorType.Invalid);
            }
            else
            {
                var table = Str(config, "table");
                if (string.IsNullOrWhiteSpace(table))
                    return PipelineRelationResult.Fail("This step has no table.", PipelineErrorType.Invalid);

                var schema = Str(config, "schema");
                // Quoted per the source's own dialect by the connection factory's rules, via the service.
                query = string.IsNullOrWhiteSpace(schema)
                    ? $"SELECT * FROM {table}"
                    : $"SELECT * FROM {schema}.{table}";
            }

            // Incremental window. Applied by WRAPPING the step's query rather than appending a WHERE:
            // the query may already have one, or a GROUP BY, or be a UNION, and appending would either be
            // a syntax error or silently filter the wrong thing.
            var incremental = PipelineIncrementalConfig.FromConfig(config, v => request.ResolveTokens(v));
            if (incremental.IsEnabled)
            {
                var ceiling = await ReadDatabaseCeilingAsync(
                    entityId, request.CompanyId, query, incremental.Column!, ct);

                if (ceiling.Failed)
                    return PipelineRelationResult.Fail(
                        $"Could not read the high-water mark for '{incremental.Column}': {ceiling.Error}",
                        PipelineErrorType.SourceUnavailable);

                var window = new PipelineWatermarkWindow
                {
                    Column = incremental.Column!,
                    Low = request.IncrementalLow ?? incremental.Start,
                    High = ceiling.Value
                };

                request.OnWindowCaptured?.Invoke(window);
                request.Progress?.WriteLine($"  incremental: {window.Describe()}");

                var predicate = window.ToPredicate();
                if (predicate is not null)
                    query = $"SELECT * FROM ({query}) _src WHERE {predicate}";
            }

            spec = new DatabaseFetchSpec
            {
                EntityId = entityId,
                Query = query,
                BatchKeyColumn = Str(config, "batchKeyColumn"),
                BatchSize = Int(config, "batchSize"),
                CommandTimeoutSeconds = Int(config, "commandTimeoutSeconds")
            };
        }

        var connection = await databaseTables.GetDecryptedConnectionAsync(spec.EntityId, request.CompanyId, ct);
        if (connection is null)
            return PipelineRelationResult.Fail(
                "That database connection is no longer configured, or this company cannot use it.",
                PipelineErrorType.SourceUnavailable);

        var temp = Path.Combine(Path.GetTempPath(), $"pipe_src_{Guid.NewGuid():N}.csv");

        try
        {
            long fetched;

            if (request.RowLimit is > 0)
            {
                // Preview. Uses the capped read rather than the streaming one, so previewing a step whose
                // source is a ten-million-row table does not pull ten million rows.
                var sample = await databaseTables.ExecuteQueryAsync(
                    spec.EntityId, request.CompanyId, spec.Query, request.RowLimit.Value, ct);

                if (sample.Error is not null)
                    return PipelineRelationResult.Fail(sample.Error, PipelineErrorType.SourceUnavailable);

                await WriteCsvAsync(temp, sample, ct);
                fetched = sample.Rows.Count;
            }
            else if (connection.DatabaseType == DataSourceType.ClickHouse
                     && !string.IsNullOrWhiteSpace(spec.BatchKeyColumn))
            {
                // ClickHouse has no ADO path here, and its non-batched reader buffers the whole result into
                // one string. Paging and streaming each page to disk keeps memory flat.
                fetched = await FetchClickHouseAsync(connection, spec, temp, request.Progress, ct);
            }
            else if (!string.IsNullOrWhiteSpace(spec.BatchKeyColumn))
            {
                fetched = await databaseTables.ReadToTempCsvBatchedAsync(
                    connection, spec.Query, spec.BatchKeyColumn!, spec.BatchSize ?? 100_000, temp, ct,
                    spec.CommandTimeoutSeconds, RowProgress(request.Progress));
            }
            else
            {
                fetched = await databaseTables.ReadToTempCsvAsync(
                    connection, spec.Query, temp, ct, spec.CommandTimeoutSeconds, RowProgress(request.Progress));
            }

            request.Progress?.WriteLine($"  fetched {fetched:N0} rows");

            // An empty result still needs a relation, or every downstream step reports a missing table
            // rather than "no rows". DuckDB infers no columns from an empty CSV, so the file is left as the
            // header-only document the reader produced.
            return await store.MaterializeFromFileAsync(
                request.ScratchDatasetId, request.Relation, temp, ImportFileFormat.Csv,
                hasHeader: true, sheet: null, rowLimit: null, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PipelineRelationResult.Fail(ex.Message, PipelineErrorType.SourceUnavailable);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task<long> FetchClickHouseAsync(
        DatabaseConnection connection, DatabaseFetchSpec spec, string destination,
        IJobProgress? progress, CancellationToken ct)
    {
        var total = 0L;
        var page = 0;

        await using var writer = new StreamWriter(destination, append: false, new UTF8Encoding(false));

        await databaseTables.ReadClickHouseBatchesAsync(
            connection, spec.Query, spec.BatchKeyColumn!, spec.BatchSize ?? 100_000,
            async (pageCsv, pageRows) =>
            {
                // Every page arrives as a standalone CSV, header included. Only the first page's header
                // belongs in the combined file.
                var body = page == 0 ? pageCsv : StripHeader(pageCsv);
                if (body.Length > 0) await writer.WriteAsync(body.AsMemory(), ct);
                if (!body.EndsWith('\n')) await writer.WriteAsync('\n');

                total += pageRows;
                page++;
                progress?.WriteLine($"  page {page}: {pageRows:N0} rows ({total:N0} so far)");
            },
            ct, spec.CommandTimeoutSeconds);

        await writer.FlushAsync(ct);
        return total;
    }

    private static string StripHeader(string csv)
    {
        var newline = csv.IndexOf('\n');
        return newline < 0 ? string.Empty : csv[(newline + 1)..];
    }

    /// <summary>
    /// Writes a capped result set as CSV for the preview path. Values are formatted invariantly, for the
    /// same reason the preview reader is: a date rendered in the server's locale would not parse back.
    /// </summary>
    private static async Task WriteCsvAsync(string path, SqlQueryResult result, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));

        var names = result.Columns.Select(c => c.Name).ToList();
        await writer.WriteLineAsync(string.Join(",", names.Select(Csv)));

        foreach (var row in result.Rows)
        {
            var cells = names.Select(n => Csv(Scalar(row.GetValueOrDefault(n))));
            await writer.WriteLineAsync(string.Join(",", cells).AsMemory(), ct);
        }

        await writer.FlushAsync(ct);
    }

    private static string Scalar(object? value) => value switch
    {
        null or DBNull => string.Empty,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static IProgress<long>? RowProgress(IJobProgress? progress)
    {
        if (progress is null) return null;

        var last = 0L;
        return new Progress<long>(rows =>
        {
            // One line per 100k rows: enough to show a long fetch is alive, not enough to bury the log.
            if (rows - last < 100_000) return;
            last = rows;
            progress.WriteLine($"  {rows:N0} rows fetched");
        });
    }

    // ---------------------------------------------------------------- lookups

    /// <summary>
    /// Accepts either an id or a name. YAML authors naturally write a name, the editor's picker stores an
    /// id, and both have to work against the same stored graph.
    /// </summary>
    private async Task<Dataset?> FindDatasetAsync(string reference, string companyId, CancellationToken ct)
    {
        var byId = await db.Dataset.AsNoTracking()
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Id == reference, ct);
        if (byId is not null) return byId;

        return await db.Dataset.AsNoTracking()
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Name == reference, ct);
    }

    private async Task<string?> ResolveEntityIdAsync(string reference, string companyId, CancellationToken ct)
    {
        var available = await databaseTables.GetConnectedDatabasesAsync(companyId, ct);

        var match = available.FirstOrDefault(o => o.Id == reference)
                    ?? available.FirstOrDefault(o =>
                        string.Equals(o.Name, reference, StringComparison.OrdinalIgnoreCase));

        return match?.Id;
    }

    // ---------------------------------------------------------------- helpers

    private static ImportFileFormat ParseFormat(string? format) => (format ?? "csv").ToLowerInvariant() switch
    {
        "tsv" => ImportFileFormat.Tsv,
        "json" => ImportFileFormat.Json,
        "parquet" => ImportFileFormat.Parquet,
        "excel" or "xlsx" => ImportFileFormat.Excel,
        _ => ImportFileFormat.Csv
    };

    private static string? Str(JsonObject? config, string key)
    {
        var value = config?[key];
        return value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    private static int? Int(JsonObject? config, string key)
    {
        var value = config?[key];
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : null;
    }

    private static List<string> StringList(JsonObject? config, string key)
    {
        var result = new List<string>();
        if (config?[key] is not JsonArray array) return result;

        foreach (var item in array)
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                result.Add(s);

        return result;
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Everything needed to pull rows out of one database connection.</summary>
    private sealed class DatabaseFetchSpec
    {
        public required string EntityId { get; init; }
        public required string Query { get; init; }
        public string? BatchKeyColumn { get; init; }
        public int? BatchSize { get; init; }
        public int? CommandTimeoutSeconds { get; init; }
    }
}

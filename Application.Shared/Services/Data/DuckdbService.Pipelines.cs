using System.Globalization;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;
using Application.Shared.Services.Data.Pipelines;
using DuckDB.NET.Data;

namespace Application.Shared.Services.Data;

/// <summary>
/// The ETL pipeline half of <see cref="DuckdbService"/> — see <see cref="IPipelineStore"/> for why these
/// live on this class rather than in a service of their own.
/// </summary>
public partial class DuckdbService
{
    /// <summary>
    /// Alias a source dataset is attached under during a copy. Fixed rather than generated because it only
    /// ever exists inside a single method call, and a stable name keeps the generated SQL readable on the
    /// step row.
    /// </summary>
    private const string AttachAlias = "_pipe_src";

    /// <summary>
    /// How many times to retry taking a DuckDB file handle. A pipeline routinely wants a file the web app
    /// is holding, because merely viewing a table in the data grid takes the read-write handle — so a busy
    /// file is an expected, transient condition rather than an error.
    /// </summary>
    private const int LockRetryAttempts = 5;
    private const int LockRetryBackoffMs = 400;

    // ------------------------------------------------------------------ materialize

    public async Task<PipelineRelationResult> MaterializeAsync(
        string datasetId, string relation, string selectSql, int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        // Re-guarded here, not only at build time: this is the last gate before execution, and it is what
        // stops a semicolon inside a free-form filter expression from stacking a second statement.
        if (!SelectOnlyGuard.IsSafeSelect(selectSql, out var guardError))
            return PipelineRelationResult.Fail(guardError ?? "Only a read-only SELECT can be run here.",
                PipelineErrorType.SqlError, selectSql);

        var sql = $"CREATE OR REPLACE TABLE {Q(relation)} AS {selectSql}";
        return await RunMaterializeAsync(datasetId, relation, sql, timeoutSeconds, ct);
    }

    public async Task<PipelineRelationResult> MaterializeFromDatasetAsync(
        string datasetId, string relation, string sourceDatasetId, string sourceTable,
        IReadOnlyList<string>? columns = null, string? where = null, int? rowLimit = null,
        int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var projection = columns is { Count: > 0 }
            ? string.Join(", ", columns.Select(Q))
            : "*";
        var filter = string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE ({where})";
        var limit = Limit(rowLimit);

        // Reading a table that already lives in this file needs no ATTACH — and must not attempt one,
        // because attaching a file the connection already holds read-write fails outright.
        if (string.Equals(datasetId, sourceDatasetId, StringComparison.Ordinal))
        {
            var localSql = $"CREATE OR REPLACE TABLE {Q(relation)} AS SELECT {projection} FROM {Q(sourceTable)}{filter}{limit}";
            return await RunMaterializeAsync(datasetId, relation, localSql, timeoutSeconds, ct);
        }

        var sourcePath = ResolveDbPath(sourceDatasetId);
        if (!File.Exists(sourcePath))
            return PipelineRelationResult.Fail(
                $"The source dataset's database was not found at '{sourcePath}'.",
                PipelineErrorType.SourceUnavailable);

        var attachSql =
            $"CREATE OR REPLACE TABLE {Q(relation)} AS " +
            $"SELECT {projection} FROM {AttachAlias}.main.{Q(sourceTable)}{filter}{limit}";

        return await RunMaterializeAsync(datasetId, relation, attachSql, timeoutSeconds, ct,
            attachSourcePath: sourcePath);
    }

    public async Task<PipelineRelationResult> MaterializeFromFileAsync(
        string datasetId, string relation, string filePath, ImportFileFormat format,
        bool hasHeader = true, string? sheet = null, int? rowLimit = null, int? timeoutSeconds = null,
        PipelineFileReadOptions? readOptions = null, bool addSourceFileColumn = false,
        string? badRowMode = null, CancellationToken ct = default)
    {
        // A wildcard is handed to DuckDB whole (the "read and combine all" option), so File.Exists would
        // be the wrong question for it.
        var isGlob = filePath.Contains('*') || filePath.Contains('?');
        if (!isGlob && !File.Exists(filePath))
            return PipelineRelationResult.Fail($"The file '{filePath}' was not found.",
                PipelineErrorType.SourceUnavailable);

        // A tolerant read is expressed to DuckDB, not implemented here. ignore_errors drops the offending
        // LINE (verified on 1.3 — it does not null the value), and store_rejects records why, which is what
        // makes a quarantine table possible at all.
        var tolerant = PipelineBadRowModes.Tolerates(badRowMode);
        var keeps = PipelineBadRowModes.Keeps(badRowMode);

        var effective = readOptions;
        if (tolerant)
        {
            effective = Clone(readOptions);
            effective.IgnoreErrors = true;

            // store_rejects for BOTH tolerant modes, not just quarantine. It is what produces the count,
            // and "skipped an unknown number of rows" is barely better than skipping them silently. The
            // difference between the two modes is whether the rejects are also kept as a table.
            effective.StoreRejects = true;
        }

        var reader = PipelineReaderExpr(format, filePath, hasHeader, sheet, effective, addSourceFileColumn);
        var sql = $"CREATE OR REPLACE TABLE {Q(relation)} AS SELECT * FROM {reader}{Limit(rowLimit)}";

        var rejectRelation = keeps ? relation + RejectSuffix : null;

        return await RunMaterializeAsync(datasetId, relation, sql, timeoutSeconds, ct,
            needsExcel: format == ImportFileFormat.Excel,
            afterMaterialize: !tolerant
                ? null
                : async (connection, token) =>
                    await CaptureRejectsAsync(connection, rejectRelation, token));
    }

    /// <summary>Suffix for the relation holding a step's rejected rows.</summary>
    private const string RejectSuffix = "__rejects";

    /// <summary>
    /// Copies DuckDB's rejected-row detail into a relation of our own, and returns how many there were.
    /// <para>
    /// This MUST run on the connection that did the read. <c>reject_errors</c> is a temporary table scoped to
    /// the connection, and every pipeline operation opens its own — so once the read returns, the evidence is
    /// gone. There is no reading it afterwards.
    /// </para>
    /// </summary>
    private static async Task<(long Rejected, string? RejectRelation)> CaptureRejectsAsync(
        DuckDBConnection connection, string? rejectRelation, CancellationToken ct)
    {
        // reject_errors only exists once a scan has registered rejects, so its absence means zero.
        long count;
        try
        {
            count = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM reject_errors", ct);
        }
        catch (Exception)
        {
            return (0, null);
        }

        if (count == 0 || rejectRelation is null) return (count, null);

        // csv_line is the row as it appeared in the file — the only form that can be corrected and reloaded.
        await ExecAsync(connection,
            $"CREATE OR REPLACE TABLE {Q(rejectRelation)} AS "
            + "SELECT line AS _line, column_name AS _column, error_type AS _reason, "
            + "error_message AS _detail, csv_line AS _raw FROM reject_errors ORDER BY line", ct);

        return (count, rejectRelation);
    }

    /// <summary>
    /// A copy, so turning on tolerance for one step cannot mutate options the caller may reuse. The node
    /// config object is shared across a run.
    /// </summary>
    private static PipelineFileReadOptions Clone(PipelineFileReadOptions? source) => new()
    {
        Delimiter = source?.Delimiter,
        Quote = source?.Quote,
        Escape = source?.Escape,
        NullString = source?.NullString,
        SkipRows = source?.SkipRows,
        Compression = source?.Compression,
        Encoding = source?.Encoding,
        DateFormat = source?.DateFormat,
        TimestampFormat = source?.TimestampFormat,
        DecimalSeparator = source?.DecimalSeparator,
        AllText = source?.AllText,
        IgnoreErrors = source?.IgnoreErrors,
        StoreRejects = source?.StoreRejects
    };

    /// <summary>Shared body: open, optionally attach, run one CTAS, then report rows and columns.</summary>
    private async Task<PipelineRelationResult> RunMaterializeAsync(
        string datasetId, string relation, string sql, int? timeoutSeconds, CancellationToken ct,
        string? attachSourcePath = null, bool needsExcel = false,
        Func<DuckDBConnection, CancellationToken, Task<(long Rejected, string? RejectRelation)>>? afterMaterialize = null)
    {
        var path = ResolveDbPath(datasetId);
        if (!File.Exists(path))
            return PipelineRelationResult.Fail($"Dataset database not found at '{path}'.",
                PipelineErrorType.Invalid, sql);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutSeconds is > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.Value));

            using var connection = await OpenWithRetryAsync(path, readOnly: false, cts.Token);
            if (needsExcel) await EnsureExcelExtensionAsync(connection, cts.Token);

            if (attachSourcePath is not null)
                await ExecAsync(connection, $"ATTACH '{Esc(attachSourcePath)}' AS {AttachAlias} (READ_ONLY)", cts.Token);

            try
            {
                await ExecAsync(connection, sql, cts.Token);
            }
            finally
            {
                // Detach even on failure, so the handle on the source file is released promptly rather
                // than at connection dispose.
                if (attachSourcePath is not null)
                {
                    try { await ExecAsync(connection, $"DETACH {AttachAlias}", cts.Token); }
                    catch { /* the connection is going away anyway */ }
                }
            }

            var rows = await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {Q(relation)}", cts.Token);
            var columns = await DescribeOnAsync(connection, relation, cts.Token);

            // Runs on THIS connection, before it closes — see CaptureRejectsAsync for why that matters.
            // The callback reports the relation it kept, rather than this method guessing a name: a skip
            // read counts rejects without keeping them, and advertising a table that does not exist would
            // send the run view looking for it.
            var (rejected, rejectRelation) = afterMaterialize is null
                ? (0L, null)
                : await afterMaterialize(connection, cts.Token);

            await connection.CloseAsync();

            return rejected == 0
                ? PipelineRelationResult.Ok(rows, columns, sql)
                : PipelineRelationResult.Ok(rows, columns, sql, rejected, rejectRelation);
        }
        catch (DuckDbBusyException ex)
        {
            return PipelineRelationResult.Fail(ex.Message, PipelineErrorType.DatasetBusy, sql);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PipelineRelationResult.Fail("The run was cancelled.", PipelineErrorType.Canceled, sql);
        }
        catch (OperationCanceledException)
        {
            return PipelineRelationResult.Fail(
                $"This step exceeded its {timeoutSeconds}s time limit.", PipelineErrorType.Timeout, sql);
        }
        catch (Exception ex)
        {
            return PipelineRelationResult.Fail(ex.Message, PipelineErrorType.SqlError, sql);
        }
    }

    // -------------------------------------------------------------------- inspect

    public async Task<List<PipelineColumn>> DescribeRelationAsync(
        string datasetId, string relation, CancellationToken ct = default)
    {
        var path = ResolveDbPath(datasetId);
        if (!File.Exists(path)) return new();

        using var connection = await OpenWithRetryAsync(path, readOnly: true, ct);
        var columns = await DescribeOnAsync(connection, relation, ct);
        await connection.CloseAsync();
        return columns;
    }

    public async Task<PipelinePreview> PreviewRelationAsync(
        string datasetId, string relation, int rows, CancellationToken ct = default)
    {
        var path = ResolveDbPath(datasetId);
        if (!File.Exists(path)) return PipelinePreview.Empty;

        var capped = Math.Clamp(rows, 1, 500);

        using var connection = await OpenWithRetryAsync(path, readOnly: true, ct);
        var columns = await DescribeOnAsync(connection, relation, ct);

        var data = new List<Dictionary<string, object?>>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT * FROM {Q(relation)} LIMIT {capped}";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Portable(reader.GetValue(i));
                data.Add(row);
            }
        }

        await connection.CloseAsync();
        return new PipelinePreview(columns, data);
    }

    /// <summary>
    /// Converts a DuckDB value into something that serializes predictably.
    /// <para>
    /// Dates and times are the reason this exists. DuckDB.NET hands back <see cref="DateOnly"/> and
    /// <see cref="TimeOnly"/>, whose default and even invariant <c>ToString()</c> is a locale-shaped short
    /// date — <c>01/05/2026</c> rather than <c>2026-01-05</c>. Left alone, a preview would render dates
    /// differently depending on the server's culture, and an exported value would not round-trip.
    /// </para>
    /// </summary>
    private static object? Portable(object? value) => value switch
    {
        null or DBNull => null,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        // Kept numeric so JSON emits a number, but widened to decimal/string where a JSON double would
        // silently lose precision on DuckDB's larger integer types.
        System.Numerics.BigInteger big => big.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value
    };

    private static async Task<List<PipelineColumn>> DescribeOnAsync(
        DuckDBConnection connection, string relation, CancellationToken ct)
    {
        var columns = new List<PipelineColumn>();
        using var cmd = connection.CreateCommand();

        // DESCRIBE SELECT rather than PRAGMA table_info: it works for a view as well as a table, and it is
        // unambiguous about which catalog the relation came from.
        cmd.CommandText = $"DESCRIBE SELECT * FROM {Q(relation)}";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(new PipelineColumn(reader.GetString(0), reader.GetString(1)));

        return columns;
    }

    // ---------------------------------------------------------------------- write

    public async Task<ImportResult> WriteRelationToTableAsync(
        string sourceDatasetId, string sourceRelation,
        string targetDatasetId, string targetTable,
        ImportMode mode, List<string> keyColumns, bool createIfMissing,
        CancellationToken ct = default)
    {
        var result = new ImportResult();

        var targetPath = ResolveDbPath(targetDatasetId);
        if (!File.Exists(targetPath))
        {
            result.Error = $"The destination dataset's database was not found at '{targetPath}'.";
            return result;
        }

        var sameFile = string.Equals(sourceDatasetId, targetDatasetId, StringComparison.Ordinal);
        var sourcePath = sameFile ? null : ResolveDbPath(sourceDatasetId);
        if (sourcePath is not null && !File.Exists(sourcePath))
        {
            result.Error = $"The pipeline's working database was not found at '{sourcePath}'.";
            return result;
        }

        // Staging lives in the TARGET file so that the promote is single-catalog and therefore able to run
        // in a transaction. Named per call so two concurrent runs writing to the same dataset cannot
        // collide on it.
        var staging = $"_pipe_in_{Guid.NewGuid():N}";

        try
        {
            using var connection = await OpenWithRetryAsync(targetPath, readOnly: false, ct);

            // 1. Bring the data into the target file. A brief ATTACH, released before anything else runs.
            if (sameFile)
            {
                await ExecAsync(connection,
                    $"CREATE OR REPLACE TABLE {Q(staging)} AS SELECT * FROM {Q(sourceRelation)}", ct);
            }
            else
            {
                await ExecAsync(connection, $"ATTACH '{Esc(sourcePath!)}' AS {AttachAlias} (READ_ONLY)", ct);
                try
                {
                    await ExecAsync(connection,
                        $"CREATE OR REPLACE TABLE {Q(staging)} AS SELECT * FROM {AttachAlias}.main.{Q(sourceRelation)}", ct);
                }
                finally
                {
                    try { await ExecAsync(connection, $"DETACH {AttachAlias}", ct); }
                    catch { /* best effort */ }
                }
            }

            // 2. From here on there is no attached catalog, which matters: TableExistsAsync queries
            //    information_schema without a catalog filter, so with something attached it could match a
            //    same-named table in the wrong database. Detaching first removes that hazard entirely.
            var exists = await TableExistsAsync(connection, targetTable, ct);

            if (!exists)
            {
                if (!createIfMissing)
                {
                    result.Error =
                        $"The destination table \"{targetTable}\" does not exist. Turn on " +
                        "\"create the table if it does not exist\" on the destination step to create it.";
                    await DropQuietlyAsync(connection, staging, ct);
                    return result;
                }

                await ExecAsync(connection,
                    $"CREATE TABLE {Q(targetTable)} AS SELECT * FROM {Q(staging)}", ct);
                result.RowsInserted = (int)await ScalarLongAsync(
                    connection, $"SELECT COUNT(*) FROM {Q(targetTable)}", ct);
                result.Success = true;

                await DropQuietlyAsync(connection, staging, ct);
                await connection.CloseAsync();
                return result;
            }

            // 3. Match the relation's columns to the target's by normalized name, the same rule file import
            //    uses — so a column named "Sale Date" lines up with the slugged column it created.
            var stagingColumns = await DescribeOnAsync(connection, staging, ct);
            var targetColumns = await ReadTargetColumnsAsync(connection, targetTable, ct);
            var stagingByKey = BuildColumnKeyMap(stagingColumns.Select(c => c.Name));

            var promoteColumns = targetColumns
                .Where(t => stagingByKey.ContainsKey(NormalizeColumnKey(t.Name)))
                .Select(t => (t.Name, t.Type, Source: Q(stagingByKey[NormalizeColumnKey(t.Name)])))
                .ToList();

            if (promoteColumns.Count == 0)
            {
                var incoming = string.Join(", ", stagingColumns.Select(c => c.Name));
                var wanted = string.Join(", ", targetColumns.Select(c => c.Name));
                result.Error =
                    $"None of the incoming columns match \"{targetTable}\". " +
                    $"Incoming: {incoming}. Expected: {wanted}. Add a Map columns step to line them up.";
                await DropQuietlyAsync(connection, staging, ct);
                return result;
            }

            // skipInvalidRows is false: a pipeline relation is already typed, so TRY_CAST would not skip
            // anything a strict CAST rejects — it would just round or null silently. Type problems belong
            // to the transform steps, where they can be reported against a specific column.
            var promoted = await PromoteRelationAsync(
                connection, Q(staging), targetTable, promoteColumns, mode, keyColumns,
                skipInvalidRows: false, "pipeline", ct);

            await DropQuietlyAsync(connection, staging, ct);
            await connection.CloseAsync();
            return promoted;
        }
        catch (DuckDbBusyException ex)
        {
            result.Error = ex.Message;
            result.ErrorType = PipelineErrorType.DatasetBusy;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "The run was cancelled.";
            result.ErrorType = PipelineErrorType.Canceled;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.ErrorType = PipelineErrorType.SqlError;
            return result;
        }
    }

    public async Task<int> DropRelationsAsync(string datasetId, string prefix, CancellationToken ct = default)
    {
        var path = ResolveDbPath(datasetId);
        if (!File.Exists(path)) return 0;

        var dropped = 0;
        try
        {
            using var connection = await OpenWithRetryAsync(path, readOnly: false, ct);

            var names = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                // starts_with rather than LIKE, deliberately. Every prefix here begins with an underscore,
                // and in LIKE an underscore is a single-character wildcard — so LIKE '_pipe%' would also
                // match a user relation named "xpipe…" and drop it. Getting the ESCAPE clause and the two
                // layers of literal escaping right is possible but easy to get subtly wrong; a function
                // with no wildcard semantics cannot be got wrong at all.
                cmd.CommandText =
                    "SELECT table_name FROM information_schema.tables " +
                    $"WHERE table_schema = 'main' AND starts_with(table_name, '{Esc(prefix)}')";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
            }

            foreach (var name in names)
            {
                await ExecAsync(connection, $"DROP TABLE IF EXISTS {Q(name)}", ct);
                dropped++;
            }

            await connection.CloseAsync();
        }
        catch
        {
            // Cleanup is best-effort by definition — it runs after something already went wrong.
        }

        return dropped;
    }

    public async Task<T> ReadRelationAsync<T>(
        string datasetId,
        string relation,
        Func<System.Data.Common.DbDataReader, List<PipelineColumn>, CancellationToken, Task<T>> consume,
        CancellationToken ct = default)
    {
        var path = ResolveDbPath(datasetId);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Dataset database not found at '{path}'.");

        // Read-only: several readers coexist on one file, so streaming a relation out does not block the
        // web app's data viewer the way a write handle would.
        using var connection = await OpenWithRetryAsync(path, readOnly: true, ct);

        var columns = await DescribeOnAsync(connection, relation, ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Q(relation)}";
        using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = await consume(reader, columns, ct);

        await connection.CloseAsync();
        return result;
    }

    // -------------------------------------------------------------------- helpers

    private static async Task DropQuietlyAsync(DuckDBConnection connection, string relation, CancellationToken ct)
    {
        try { await ExecAsync(connection, $"DROP TABLE IF EXISTS {Q(relation)}", ct); }
        catch { /* leftover staging is swept later; never fail a successful write over it */ }
    }

    /// <summary>
    /// A LIMIT clause, or nothing. Used to sample a source during a preview so that previewing a step whose
    /// source is a very large table stays cheap.
    /// </summary>
    private static string Limit(int? rows) => rows is > 0 ? $" LIMIT {rows.Value}" : string.Empty;

    /// <summary>Escapes a value for a single-quoted DuckDB string literal, including Windows backslashes.</summary>
    private static string Esc(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "''");

    /// <summary>
    /// Opens a connection, retrying while the file is locked by another handle.
    /// <para>
    /// Worth being explicit about what this can and cannot fix. DuckDB allows one read-write handle per
    /// file, and the web app takes that handle just by rendering a table in the data viewer — so losing the
    /// race is normal and usually over in moments. What it cannot fix is a genuinely long-held handle; in
    /// that case it gives up with a message naming the dataset rather than surfacing DuckDB's raw
    /// "File is already open" and a process id.
    /// </para>
    /// </summary>
    private static async Task<DuckDBConnection> OpenWithRetryAsync(
        string path, bool readOnly, CancellationToken ct)
    {
        var connectionString = readOnly
            ? $"DataSource={path};ACCESS_MODE=READ_ONLY"
            : $"DataSource={path}";

        for (var attempt = 1; ; attempt++)
        {
            var connection = new DuckDBConnection(connectionString);
            try
            {
                await connection.OpenAsync(ct);
                return connection;
            }
            catch (Exception ex) when (IsFileLocked(ex) && attempt < LockRetryAttempts)
            {
                await connection.DisposeAsync();
                await Task.Delay(LockRetryBackoffMs * attempt, ct);
            }
            catch (Exception ex) when (IsFileLocked(ex))
            {
                await connection.DisposeAsync();
                throw new DuckDbBusyException(
                    "This dataset is in use by another process and did not become available. " +
                    "Close any open data view of it, or wait for the running job to finish, and try again.", ex);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// Reads a single value from a dataset with a READ-ONLY handle.
    /// <para>
    /// Read-only matters more than it looks: this runs against a dataset a person may well have open in the
    /// data viewer, and taking the write handle to ask for a MAX would fail the run for no reason. Guarded
    /// by <see cref="SelectOnlyGuard"/> because the caller composes the SQL from a column name.
    /// </para>
    /// </summary>
    public async Task<PipelineScalarResult> ReadScalarAsync(
        string datasetId, string sql, int? timeoutSeconds = null, CancellationToken ct = default)
    {
        if (!SelectOnlyGuard.IsSafeSelect(sql, out var guardError))
            return PipelineScalarResult.Fail(guardError ?? "Only a read-only SELECT can be run here.");

        var path = ResolveDbPath(datasetId);
        if (!File.Exists(path))
            return PipelineScalarResult.Fail($"Dataset database not found at '{path}'.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? _option.ResolveQueryTimeoutSeconds()));

        try
        {
            await using var connection = await OpenWithRetryAsync(path, readOnly: true, cts.Token);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;

            await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
            if (!await reader.ReadAsync(cts.Token)) return PipelineScalarResult.Ok(null, null);

            var value = reader.IsDBNull(0) ? null : reader.GetValue(0);
            return PipelineScalarResult.Ok(value, reader.GetDataTypeName(0));
        }
        catch (DuckDbBusyException ex)
        {
            return PipelineScalarResult.Fail(ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PipelineScalarResult.Fail("Reading the incremental high-water mark timed out.");
        }
        catch (Exception ex)
        {
            return PipelineScalarResult.Fail(ex.Message.Split('\n')[0]);
        }
    }

    private static bool IsFileLocked(Exception ex) =>
        ex.Message.Contains("already open", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Could not set lock", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Conflicting lock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reader expression for a pipeline file source. Deliberately separate from
    /// <c>InferringReaderExpr</c>, which is tuned for the import wizard's staging step: this one honours a
    /// header toggle, an Excel sheet name and the delimited-file parse options, none of which the wizard
    /// exposes.
    /// <para>
    /// With no <paramref name="options"/> this emits exactly what it emitted before parse options existed,
    /// so an untouched pipeline keeps producing identical output. Each option only appears when set.
    /// </para>
    /// </summary>
    private static string PipelineReaderExpr(
        ImportFileFormat format, string path, bool hasHeader, string? sheet,
        PipelineFileReadOptions? options = null, bool addSourceFileColumn = false)
    {
        var p = Esc(path);
        var header = hasHeader ? "true" : "false";

        // filename=true asks DuckDB for the source path as a column. Free — no second pass, and it is the
        // only way to get it when a glob has matched several files.
        var filename = addSourceFileColumn ? ", filename=true" : string.Empty;

        return format switch
        {
            ImportFileFormat.Json => $"read_json_auto('{p}')",
            ImportFileFormat.Parquet => $"read_parquet('{p}'{filename})",

            // Excel has no delimiter or quoting to configure, so the parse options do not apply.
            ImportFileFormat.Excel => string.IsNullOrWhiteSpace(sheet)
                ? $"read_xlsx('{p}', header={header})"
                : $"read_xlsx('{p}', header={header}, sheet='{Esc(sheet!)}')",

            // TSV is CSV with a known delimiter, which an explicit option may still override.
            ImportFileFormat.Tsv =>
                $"read_csv('{p}', header={header}{CsvOptions(options, defaultDelimiter: "\\t")}{filename})",

            // read_csv rather than read_csv_auto once anything is specified: the _auto variant is documented
            // to ignore what it can re-detect, which would silently drop the very option that was set.
            _ => options is null || options.IsEmpty
                ? $"read_csv_auto('{p}', header={header}, sample_size=-1{filename})"
                : $"read_csv('{p}', header={header}, sample_size=-1{CsvOptions(options, null)}{filename})"
        };
    }

    /// <summary>
    /// The configured parse options as DuckDB named arguments, each omitted when unset.
    /// </summary>
    private static string CsvOptions(PipelineFileReadOptions? options, string? defaultDelimiter)
    {
        var parts = new List<string>();

        var delimiter = options?.Delimiter;
        if (!string.IsNullOrEmpty(delimiter)) parts.Add($"delim='{EscapeCsvArg(delimiter!)}'");
        else if (defaultDelimiter is not null) parts.Add($"delim='{defaultDelimiter}'");

        if (options is null) return Join(parts);

        // An empty quote or escape is meaningful — it turns the feature off — so these test for null, not
        // for emptiness.
        if (options.Quote is not null) parts.Add($"quote='{EscapeCsvArg(options.Quote)}'");
        if (options.Escape is not null) parts.Add($"escape='{EscapeCsvArg(options.Escape)}'");
        if (options.NullString is not null) parts.Add($"nullstr='{EscapeCsvArg(options.NullString)}'");

        if (options.SkipRows is int skip && skip > 0) parts.Add($"skip={skip}");

        if (!string.IsNullOrWhiteSpace(options.Compression))
            parts.Add($"compression='{EscapeCsvArg(options.Compression!)}'");
        if (!string.IsNullOrWhiteSpace(options.Encoding))
            parts.Add($"encoding='{EscapeCsvArg(options.Encoding!)}'");
        if (!string.IsNullOrWhiteSpace(options.DateFormat))
            parts.Add($"dateformat='{EscapeCsvArg(options.DateFormat!)}'");
        if (!string.IsNullOrWhiteSpace(options.TimestampFormat))
            parts.Add($"timestampformat='{EscapeCsvArg(options.TimestampFormat!)}'");
        if (!string.IsNullOrWhiteSpace(options.DecimalSeparator))
            parts.Add($"decimal_separator='{EscapeCsvArg(options.DecimalSeparator!)}'");

        if (options.AllText == true) parts.Add("all_varchar=true");
        if (options.IgnoreErrors == true) parts.Add("ignore_errors=true");
        if (options.StoreRejects == true) parts.Add("store_rejects=true");

        return Join(parts);

        static string Join(List<string> items) =>
            items.Count == 0 ? string.Empty : ", " + string.Join(", ", items);
    }

    /// <summary>
    /// Quotes a single-character-ish DuckDB argument. A literal tab typed as the two characters <c>\t</c>
    /// is passed through as DuckDB's own escape rather than being mangled into a backslash and a t.
    /// </summary>
    private static string EscapeCsvArg(string value) =>
        value == "\\t" ? "\\t" : value.Replace("\\", "\\\\").Replace("'", "''");
}

/// <summary>
/// A DuckDB file could not be opened because another handle holds it. Distinguished from a generic failure
/// so the run inspector can say "the dataset is busy" — which is a retry, not a bug.
/// </summary>
public sealed class DuckDbBusyException(string message, Exception inner) : Exception(message, inner);

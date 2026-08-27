using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Writes a pipeline's final relation into a table in an EXTERNAL database — the destination path for a
/// dataset whose <c>SourceType</c> is External.
/// <para>
/// Three things shape this class:
/// </para>
/// <list type="bullet">
/// <item><b>It needs its own credential.</b> The stored <see cref="DatabaseConnection"/> is the
/// least-privilege reader and is built with read-only intent everywhere; the admin credential is a DBA
/// account. Neither is right for loading a table, so a write must have a
/// <see cref="DatabaseWriteCredential"/> and is refused outright without one — never silently escalated.
/// That is the same rule <c>DatabaseAdminService.LoadAdminConnectionAsync</c> follows.</item>
/// <item><b>Rows stream.</b> Every provider is fed the live DuckDB reader, so a hundred-million-row load
/// never materialises in memory.</item>
/// <item><b>Upsert is honest about where it works.</b> It is implemented as staging + delete + insert, which
/// behaves identically on SQL Server, PostgreSQL and MySQL. ClickHouse deletes asynchronously through
/// ALTER TABLE, so an upsert there would appear to succeed and then not have happened — it is refused
/// instead.</item>
/// </list>
/// </summary>
public interface IExternalTableWriter
{
    Task<ImportResult> WriteAsync(ExternalWriteRequest request, CancellationToken ct = default);
}

public sealed class ExternalWriteRequest
{
    /// <summary>The Database-type entity behind the destination dataset.</summary>
    public required string EntityId { get; init; }
    public required string CompanyId { get; init; }

    /// <summary>The scratch dataset holding the relation to write.</summary>
    public required string SourceDatasetId { get; init; }
    public required string SourceRelation { get; init; }

    public string? Schema { get; init; }
    public required string Table { get; init; }

    public ImportMode Mode { get; init; } = ImportMode.Append;
    public List<string> KeyColumns { get; init; } = new();
    public bool CreateIfMissing { get; init; }
    public int BatchSize { get; init; } = 10_000;

    public IJobProgress? Progress { get; init; }
}

public class ExternalTableWriter(
    StatusDbContext status,
    IPipelineStore store,
    IDatabaseTableService databaseTables,
    ICredentialProtector protector,
    IHttpClientFactory httpClientFactory) : IExternalTableWriter
{
    public async Task<ImportResult> WriteAsync(ExternalWriteRequest request, CancellationToken ct = default)
    {
        var result = new ImportResult();

        // The read-only connection supplies WHERE the server is; the write credential supplies WHO connects.
        var connection = await databaseTables.GetDecryptedConnectionAsync(
            request.EntityId, request.CompanyId, ct);

        if (connection is null)
        {
            result.Error = "That database connection is no longer configured.";
            result.ErrorType = PipelineErrorType.SourceUnavailable;
            return result;
        }

        var credential = await status.DatabaseWriteCredentials.AsNoTracking()
            .FirstOrDefaultAsync(w => w.EntityId == request.EntityId, ct);

        if (credential is null || string.IsNullOrWhiteSpace(credential.Username))
        {
            // Refused rather than falling back to the reader. Silently escalating the least-privilege
            // account is exactly what the separate credential exists to prevent.
            result.Error =
                "No write credential is configured for this database. An administrator must add one before " +
                "a pipeline can write into it — the read-only connection deliberately cannot.";
            result.ErrorType = PipelineErrorType.NotWritable;
            return result;
        }

        if (request.CreateIfMissing && !credential.AllowCreateTable)
        {
            result.Error =
                "This step is set to create the table, but the write credential for this database does not " +
                "permit creating tables. Create it manually, or have an administrator allow it.";
            result.ErrorType = PipelineErrorType.NotWritable;
            return result;
        }

        // Substitute the writer's identity. Host, port, catalog and SSL still come from the entity, so there
        // is one source of truth for where the server is.
        var target = new DatabaseConnection
        {
            Id = connection.Id,
            EntityId = connection.EntityId,
            DatabaseType = connection.DatabaseType,
            Host = connection.Host,
            Port = connection.Port,
            DatabaseName = connection.DatabaseName,
            UseSsl = connection.UseSsl,
            FilePath = connection.FilePath,
            Username = credential.Username,
            // Decrypted in place, matching what GetDecryptedConnectionAsync does to the reader.
            SecretEncrypted = protector.Decrypt(credential.SecretEncrypted ?? string.Empty)
        };

        if (target.DatabaseType == DataSourceType.ClickHouse
            && request.Mode == ImportMode.Upsert)
        {
            result.Error =
                "Upsert is not supported for ClickHouse: it deletes asynchronously, so the write would " +
                "appear to succeed before the old rows were gone. Use append or replace.";
            result.ErrorType = PipelineErrorType.NotWritable;
            return result;
        }

        try
        {
            return target.DatabaseType switch
            {
                DataSourceType.SQLServer or DataSourceType.PostgreSQL or DataSourceType.MySQL =>
                    await WriteAdoAsync(target, request, ct),

                DataSourceType.ClickHouse => await WriteClickHouseAsync(target, request, ct),

                _ => Unsupported(target.DatabaseType)
            };
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

    private static ImportResult Unsupported(DataSourceType type) => new()
    {
        Error = $"Writing into a {type} database is not supported.",
        ErrorType = PipelineErrorType.NotWritable
    };

    // ------------------------------------------------------------------ ADO providers

    private async Task<ImportResult> WriteAdoAsync(
        DatabaseConnection target, ExternalWriteRequest request, CancellationToken ct)
    {
        var result = new ImportResult();
        var engine = target.DatabaseType;
        var qualified = SqlTypeMapper.QualifiedTable(engine, request.Schema, request.Table);

        // Columns first, from a short read, so DDL and the staging table can be prepared before the bulk
        // load opens its own reader.
        var columns = await store.ReadRelationAsync(
            request.SourceDatasetId, request.SourceRelation,
            (_, cols, _) => Task.FromResult(cols), ct);

        if (columns.Count == 0)
        {
            result.Error = "The incoming data has no columns.";
            result.ErrorType = PipelineErrorType.Invalid;
            return result;
        }

        await using var db = ExternalConnectionFactory.CreateForCatalog(
            target, null, readOnly: false, forBulkLoad: engine == DataSourceType.MySQL);

        await db.OpenAsync(ct);

        if (!await TableExistsAsync(db, engine, request.Schema, request.Table, ct))
        {
            if (!request.CreateIfMissing)
            {
                result.Error =
                    $"The destination table {qualified} does not exist. Create it, or turn on " +
                    "\"create the table if it does not exist\" on the destination step.";
                result.ErrorType = PipelineErrorType.NotWritable;
                return result;
            }

            var ddl = BuildCreateTable(engine, qualified, columns);
            if (ddl is null)
            {
                result.Error =
                    "Could not work out a column type for every column, so the table was not created. " +
                    "Create it manually and run again.";
                result.ErrorType = PipelineErrorType.NotWritable;
                return result;
            }

            request.Progress?.WriteLine($"      creating {qualified}");
            await ExecuteAsync(db, ddl, ct);
        }

        // Where the rows actually land: the target for append/replace, a staging table for upsert.
        var staging = request.Mode == ImportMode.Upsert
            ? SqlTypeMapper.QualifiedTable(engine, request.Schema, "_pipe_stg_" + Guid.NewGuid().ToString("N")[..12])
            : null;

        if (staging is not null)
        {
            await ExecuteAsync(db, CreateLike(engine, staging, qualified), ct);
        }
        else if (request.Mode == ImportMode.Replace)
        {
            // DELETE rather than TRUNCATE: TRUNCATE needs higher privileges and cannot run inside a
            // transaction on some engines, and a write credential should not need to be that powerful.
            request.Progress?.WriteLine($"      clearing {qualified}");
            await ExecuteAsync(db, $"DELETE FROM {qualified}", ct);
        }

        try
        {
            var destination = staging ?? qualified;
            var written = await BulkLoadAsync(db, engine, destination, columns, request, ct);

            if (staging is null)
            {
                result.RowsInserted = (int)Math.Min(written, int.MaxValue);
                result.Success = true;
                return result;
            }

            // Upsert: delete the matching rows, then move the staged rows across. Two statements rather than
            // a MERGE, because the syntax is identical on all three engines and MERGE is not.
            var keys = ResolveKeys(request.KeyColumns, columns, engine);
            if (keys.Count == 0)
            {
                result.Error = "Upsert needs at least one key column that exists in the incoming data.";
                result.ErrorType = PipelineErrorType.Invalid;
                return result;
            }

            var match = string.Join(" AND ", keys.Select(k => $"t.{k} = s.{k}"));
            var columnList = string.Join(", ", columns.Select(c => SqlTypeMapper.Quote(engine, c.Name)));

            request.Progress?.WriteLine($"      merging {written:N0} staged rows on {string.Join(", ", keys)}");

            await ExecuteAsync(db,
                $"DELETE t FROM {qualified} t INNER JOIN {staging} s ON {match}", ct,
                // SQL Server accepts the DELETE ... FROM ... JOIN form; the others need a subquery.
                fallback: $"DELETE FROM {qualified} WHERE EXISTS (SELECT 1 FROM {staging} s WHERE " +
                          string.Join(" AND ", keys.Select(k => $"{qualified}.{k} = s.{k}")) + ")");

            await ExecuteAsync(db,
                $"INSERT INTO {qualified} ({columnList}) SELECT {columnList} FROM {staging}", ct);

            result.RowsInserted = (int)Math.Min(written, int.MaxValue);
            result.Success = true;
            return result;
        }
        finally
        {
            if (staging is not null)
            {
                try { await ExecuteAsync(db, $"DROP TABLE {staging}", ct); }
                catch { /* a leftover staging table is untidy, never a reason to fail a good write */ }
            }
        }
    }

    /// <summary>
    /// Hands the live DuckDB reader straight to the provider's bulk-copy API. Nothing is buffered: the
    /// reader is pulled as fast as the destination accepts rows.
    /// </summary>
    private async Task<long> BulkLoadAsync(
        DbConnection db, DataSourceType engine, string destination,
        List<PipelineColumn> columns, ExternalWriteRequest request, CancellationToken ct)
    {
        return await store.ReadRelationAsync(request.SourceDatasetId, request.SourceRelation,
            async (reader, cols, token) =>
            {
                switch (engine)
                {
                    case DataSourceType.SQLServer:
                    {
                        using var bulk = new SqlBulkCopy((SqlConnection)db)
                        {
                            DestinationTableName = destination,
                            BatchSize = request.BatchSize,
                            BulkCopyTimeout = 0            // the run-level token is the real deadline
                        };

                        // Explicit mappings by name: without them SqlBulkCopy maps by ordinal, so a
                        // destination whose columns are in a different order loads the wrong values into
                        // the right-looking columns.
                        foreach (var column in cols)
                            bulk.ColumnMappings.Add(column.Name, column.Name);

                        await bulk.WriteToServerAsync(reader, token);
                        return CountOf(reader);
                    }

                    case DataSourceType.MySQL:
                    {
                        var bulk = new MySqlBulkCopy((MySqlConnection)db)
                        {
                            DestinationTableName = destination,
                            BulkCopyTimeout = 0
                        };

                        for (var i = 0; i < cols.Count; i++)
                            bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, cols[i].Name));

                        var copied = await bulk.WriteToServerAsync(reader, token);
                        return copied.RowsInserted;
                    }

                    case DataSourceType.PostgreSQL:
                    {
                        // Binary COPY, the fastest path Npgsql offers. Row-by-row because COPY has no
                        // reader overload, but still streaming — nothing accumulates.
                        var columnList = string.Join(", ",
                            cols.Select(c => SqlTypeMapper.Quote(engine, c.Name)));

                        await using var writer = await ((NpgsqlConnection)db)
                            .BeginBinaryImportAsync(
                                $"COPY {destination} ({columnList}) FROM STDIN (FORMAT BINARY)", token);

                        long rows = 0;
                        while (await reader.ReadAsync(token))
                        {
                            await writer.StartRowAsync(token);
                            for (var i = 0; i < cols.Count; i++)
                            {
                                if (await reader.IsDBNullAsync(i, token))
                                    await writer.WriteNullAsync(token);
                                else
                                    await writer.WriteAsync(reader.GetValue(i), token);
                            }
                            rows++;
                        }

                        await writer.CompleteAsync(token);
                        return rows;
                    }

                    default:
                        throw new NotSupportedException($"No bulk loader for {engine}.");
                }
            }, ct);
    }

    /// <summary>
    /// SqlBulkCopy and MySqlBulkCopy consume the reader themselves and do not always report a count, so the
    /// rows actually transferred are taken from the reader afterwards where the provider exposes it.
    /// </summary>
    private static long CountOf(DbDataReader reader) =>
        reader is DuckDB.NET.Data.DuckDBDataReader ? Math.Max(0, reader.RecordsAffected) : 0;

    // ---------------------------------------------------------------- ClickHouse

    /// <summary>
    /// Which ClickHouse database this write targets.
    /// <para>
    /// It comes from the entity's own connection, NOT from the FlowByte dataset's display name — those are
    /// different things and only coincidentally similar. Getting this wrong is silent: ClickHouse simply
    /// resolves an unqualified name against the session default, so rows land in <c>default</c> and the run
    /// reports success against a table nobody was looking at.
    /// </para>
    /// <para>
    /// Precedence: an explicit schema on the step wins, then the connection's database, then ClickHouse's
    /// own fallback.
    /// </para>
    /// </summary>
    internal static string ClickHouseDatabase(DatabaseConnection target, ExternalWriteRequest request) =>
        !string.IsNullOrWhiteSpace(request.Schema) ? request.Schema!
        : !string.IsNullOrWhiteSpace(target.DatabaseName) ? target.DatabaseName!
        : "default";

    /// <summary>
    /// The fully-qualified target. Qualified explicitly rather than relying on the connection's default
    /// database, so the statement says where it is going and cannot be redirected by session state.
    /// </summary>
    internal static string ClickHouseTable(DatabaseConnection target, ExternalWriteRequest request) =>
        SqlTypeMapper.Quote(DataSourceType.ClickHouse, ClickHouseDatabase(target, request))
        + "." + SqlTypeMapper.Quote(DataSourceType.ClickHouse, request.Table);


    /// <summary>
    /// ClickHouse has no ADO path from a stored connection (the connection factory declines it), so this
    /// posts <c>INSERT ... FORMAT CSVWithNames</c> over HTTP — the same mechanism
    /// <c>DatabaseAdminService</c> already uses for write-capable ClickHouse calls. Rows are sent in
    /// batches so a large load never builds one enormous request body.
    /// </summary>
    private async Task<ImportResult> WriteClickHouseAsync(
        DatabaseConnection target, ExternalWriteRequest request, CancellationToken ct)
    {
        var result = new ImportResult();

        // QualifiedTable deliberately drops the schema for ClickHouse (its "schema" slot IS the database),
        // so the database has to be applied here instead — otherwise every write lands in `default`.
        var table = ClickHouseTable(target, request);

        request.Progress?.WriteLine(
            $"      target {table} on {target.Host}");

        // Columns first, from a short read. The ADO path does the same, and this side needs them for the
        // same reason: the DDL has to be built before anything is sent.
        var columns = await store.ReadRelationAsync(
            request.SourceDatasetId, request.SourceRelation,
            (_, cols, _) => Task.FromResult(cols), ct);

        if (columns.Count == 0)
        {
            result.Error = "The incoming data has no columns.";
            result.ErrorType = PipelineErrorType.Invalid;
            return result;
        }

        if (request.CreateIfMissing)
        {
            var ddl = BuildCreateTable(DataSourceType.ClickHouse, table, columns);

            if (ddl is null)
            {
                result.Error =
                    "Could not work out a ClickHouse column type for every column, so the table was not " +
                    "created. Create it manually and run again.";
                result.ErrorType = PipelineErrorType.NotWritable;
                return result;
            }

            // The DDL is already CREATE TABLE IF NOT EXISTS, so this is idempotent and needs no prior
            // existence check — which is just as well, since ClickHouse has no ADO path to ask over.
            request.Progress?.WriteLine($"      ensuring {table} exists");
            await PostClickHouseAsync(target, ddl, ct);
        }
        else if (!await ClickHouseTableExistsAsync(target, request, ct))
        {
            // Without this the INSERT below fails with ClickHouse's raw UNKNOWN_TABLE, which says nothing
            // about the setting that would have fixed it.
            result.Error =
                $"The destination table {table} does not exist. Create it, or turn on \"create the table " +
                "if it does not exist\" on the destination step.";
            result.ErrorType = PipelineErrorType.NotWritable;
            return result;
        }

        // After the create, not before: a Replace into a table that did not exist yet would otherwise
        // truncate nothing and then fail.
        if (request.Mode == ImportMode.Replace)
        {
            request.Progress?.WriteLine($"      truncating {table}");
            await PostClickHouseAsync(target, $"TRUNCATE TABLE IF EXISTS {table}", ct);
        }

        var total = await store.ReadRelationAsync(request.SourceDatasetId, request.SourceRelation,
            async (reader, cols, token) =>
            {
                var header = string.Join(",", cols.Select(c => Csv(c.Name)));
                var body = new StringBuilder();
                var inBatch = 0;
                long rows = 0;

                async Task FlushAsync()
                {
                    if (inBatch == 0) return;

                    var sql = $"INSERT INTO {table} FORMAT CSVWithNames\n{header}\n{body}";
                    await PostClickHouseAsync(target, sql, token);

                    request.Progress?.WriteLine($"      sent {rows:N0} rows");
                    body.Clear();
                    inBatch = 0;
                }

                while (await reader.ReadAsync(token))
                {
                    var cells = new List<string>(cols.Count);
                    for (var i = 0; i < cols.Count; i++)
                        cells.Add(await reader.IsDBNullAsync(i, token)
                            ? "\\N"                      // ClickHouse's CSV null
                            : Csv(Scalar(reader.GetValue(i))));

                    body.Append(string.Join(",", cells)).Append('\n');
                    rows++;

                    if (++inBatch >= Math.Max(1000, request.BatchSize)) await FlushAsync();
                }

                await FlushAsync();
                return rows;
            }, ct);

        result.RowsInserted = (int)Math.Min(total, int.MaxValue);
        result.Success = true;
        return result;
    }

    /// <summary>
    /// Whether a table exists, asked of ClickHouse's own catalogue over HTTP.
    /// <para>
    /// Only needed when the step is NOT set to create the table: the create path uses
    /// <c>CREATE TABLE IF NOT EXISTS</c> and does not care. A failure to answer is treated as "exists", so
    /// a catalogue quirk cannot block a load that would otherwise have worked — the INSERT will report the
    /// real problem either way.
    /// </para>
    /// </summary>
    private async Task<bool> ClickHouseTableExistsAsync(
        DatabaseConnection target, ExternalWriteRequest request, CancellationToken ct)
    {
        var database = ClickHouseDatabase(target, request);

        var sql =
            "SELECT count() FROM system.tables WHERE database = " +
            $"'{database.Replace("'", "\\'")}' AND name = '{request.Table.Replace("'", "\\'")}'";

        try
        {
            var body = await PostClickHouseAsync(target, sql, ct);
            return !long.TryParse(body?.Trim(), out var count) || count > 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Posts a statement to ClickHouse over HTTP.
    /// <para>
    /// A deliberate near-duplicate of the private helper in <c>DatabaseAdminService</c>, for one reason: that
    /// one caps its client at two minutes, which is right for a DDL statement and far too short for a bulk
    /// load. This one leaves the deadline to the run-level cancellation token, which is the real limit. No
    /// <c>?readonly=1</c>, since this writes.
    /// </para>
    /// </summary>
    private async Task<string?> PostClickHouseAsync(DatabaseConnection c, string sql, CancellationToken ct)
    {
        var protocol = c.UseSsl ? "https" : "http";
        var port = c.Port > 0 ? c.Port : 8123;

        var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        // The database goes on the query string. Without it ClickHouse uses the session default — which is
        // how a write configured for one database silently populates `default` instead.
        var database = string.IsNullOrWhiteSpace(c.DatabaseName)
            ? string.Empty
            : "?database=" + Uri.EscapeDataString(c.DatabaseName!);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{protocol}://{c.Host}:{port}/{database}");

        if (!string.IsNullOrEmpty(c.Username))
        {
            // SecretEncrypted holds the DECRYPTED password by this point, matching the convention the rest
            // of the external-database code follows.
            var auth = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{c.Username}:{c.SecretEncrypted}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        request.Content = new StringContent(sql, Encoding.UTF8, "text/plain");

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"ClickHouse rejected the write ({(int)response.StatusCode}): {body}");

        // Returned so a SELECT can be read back — the existence check needs an answer, not just a
        // non-failure.
        return body;
    }

    // -------------------------------------------------------------------- helpers

    private static async Task<bool> TableExistsAsync(
        DbConnection db, DataSourceType engine, string? schema, string table, CancellationToken ct)
    {
        var sql = engine switch
        {
            DataSourceType.SQLServer =>
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @t" +
                (string.IsNullOrWhiteSpace(schema) ? "" : " AND TABLE_SCHEMA = @s"),
            _ =>
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t" +
                (string.IsNullOrWhiteSpace(schema) ? "" : " AND table_schema = @s")
        };

        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;

        var t = cmd.CreateParameter();
        t.ParameterName = "@t";
        t.Value = table;
        cmd.Parameters.Add(t);

        if (!string.IsNullOrWhiteSpace(schema))
        {
            var s = cmd.CreateParameter();
            s.ParameterName = "@s";
            s.Value = schema;
            cmd.Parameters.Add(s);
        }

        var count = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(count ?? 0) > 0;
    }

    /// <summary>
    /// Internal rather than private so the destination DDL can be asserted without a live database.
    /// This runs against a customer's database and had no coverage at all until it failed in use.
    /// </summary>
    internal static string? BuildCreateTable(
        DataSourceType engine, string qualified, List<PipelineColumn> columns)
    {
        // ClickHouse expresses nullability in the TYPE (Nullable(Int32)), so it takes no NULL suffix.
        // Built per column rather than stripped from the finished string afterwards: a Replace(" NULL")
        // over the whole body would also cut those characters out of a quoted column name that happened
        // to contain them.
        var clickHouse = engine == DataSourceType.ClickHouse;
        var parts = new List<string>();

        foreach (var column in columns)
        {
            var type = SqlTypeMapper.For(engine, column.Type);
            if (type is null) return null;         // no safe mapping: refuse rather than guess

            var name = SqlTypeMapper.Quote(engine, column.Name);
            parts.Add(clickHouse ? $"{name} {type}" : $"{name} {type} NULL");
        }

        var body = string.Join(", ", parts);

        // ClickHouse needs an engine and an ordering key; MergeTree with no sorting key is the neutral
        // choice for a load target. IF NOT EXISTS makes the create idempotent, which is what lets the
        // ClickHouse write path skip an existence check entirely.
        return clickHouse
            ? $"CREATE TABLE IF NOT EXISTS {qualified} ({body}) ENGINE = MergeTree ORDER BY tuple()"
            : $"CREATE TABLE {qualified} ({body})";
    }

    private static string CreateLike(DataSourceType engine, string staging, string target) => engine switch
    {
        // A structure-only copy. SELECT INTO with a false predicate is the portable form for SQL Server;
        // the other two have a dedicated syntax.
        DataSourceType.SQLServer => $"SELECT * INTO {staging} FROM {target} WHERE 1 = 0",
        DataSourceType.PostgreSQL => $"CREATE TABLE {staging} (LIKE {target})",
        _ => $"CREATE TABLE {staging} LIKE {target}"
    };

    private static List<string> ResolveKeys(
        List<string> requested, List<PipelineColumn> columns, DataSourceType engine)
    {
        var available = columns.ToDictionary(c => c.Name, c => c.Name, StringComparer.OrdinalIgnoreCase);

        return requested
            .Where(k => available.ContainsKey(k))
            .Select(k => SqlTypeMapper.Quote(engine, available[k]))
            .ToList();
    }

    /// <summary>
    /// Runs a statement, optionally retrying with a dialect-specific alternative. The upsert delete has one
    /// form SQL Server accepts and another the rest do, and trying both is simpler and clearer than a
    /// per-engine branch that has to be kept in sync with the statement it builds.
    /// </summary>
    private static async Task ExecuteAsync(
        DbConnection db, string sql, CancellationToken ct, string? fallback = null)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch when (fallback is not null)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = fallback;
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Invariant formatting, for the same reason the preview reader uses it: a locale-shaped date
    /// would not parse back on the far side.</summary>
    private static string Scalar(object? value) => value switch
    {
        null or DBNull => string.Empty,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}

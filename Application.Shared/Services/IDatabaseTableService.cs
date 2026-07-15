using Application.Shared.Models;
using Application.Shared.Models.Data;

namespace Application.Shared.Services;

/// <summary>
/// Manages a Database-type entity's connection details and enumerates its tables,
/// materializing the chosen ones as Table entities with a dependency on the database.
/// </summary>
public interface IDatabaseTableService
{
    Task<DatabaseConnectionDto?> GetConnectionAsync(string entityId, string companyId, CancellationToken ct = default);
    Task<DatabaseConnectionDto> SaveConnectionAsync(string entityId, string companyId, DatabaseConnectionRequest request, string? modifiedBy, CancellationToken ct = default);
    Task<bool> DeleteConnectionAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Opens the configured connection and runs a trivial query; returns ok + any error message.</summary>
    Task<DatabaseConnectionTestResult> TestConnectionAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Lists the database's tables as {schema}.{name}, matched to existing Table entities. Sets Error (no throw) on connection failure.</summary>
    Task<DatabaseTableDiscoveryDto> DiscoverTablesAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Lists the company's Database-type entities that have a saved connection — the candidates a dataset can be backed by.</summary>
    Task<List<DatabaseEntityOptionDto>> GetConnectedDatabasesAsync(string companyId, CancellationToken ct = default);

    /// <summary>Runs a read-only SELECT over the configured connection of a Database entity and returns the
    /// result grid (columns + rows), capped at <paramref name="maxRows"/>. Non-SELECT statements and
    /// connection/query failures are returned via <see cref="SqlQueryResult.Error"/>, never thrown.</summary>
    Task<SqlQueryResult> ExecuteQueryAsync(string entityId, string companyId, string sql, int maxRows, CancellationToken ct = default);

    /// <summary>Reads up to <paramref name="maxRows"/> sample rows from a source table/view using a
    /// <b>server-side</b> row limit (dialect-correct <c>TOP</c>/<c>LIMIT</c>), so a heavy view isn't fully
    /// evaluated just to return a handful of rows. Columns + rows land in the result; failures are returned
    /// via <see cref="SqlQueryResult.Error"/>, never thrown.</summary>
    Task<SqlQueryResult> GetTableSampleAsync(string entityId, string companyId, string tableName, int maxRows, CancellationToken ct = default);

    /// <summary>Reads only the column shape (name + type) of a source table/view <b>without executing the
    /// query or transferring rows</b> — a schema-only describe (<c>CommandBehavior.SchemaOnly</c> for ADO
    /// engines, <c>DESCRIBE TABLE</c> for ClickHouse). Far cheaper than SELECT-ing a row for schema discovery,
    /// especially against heavy views. Columns land in <see cref="SqlQueryResult.Columns"/>; failures are
    /// returned via <see cref="SqlQueryResult.Error"/>, never thrown.</summary>
    Task<SqlQueryResult> GetTableSchemaAsync(string entityId, string companyId, string tableName, CancellationToken ct = default);

    /// <summary>Creates/updates Table entities for the chosen tables and wires each Table → Database dependency.</summary>
    Task<DatabaseTableCommitResult> CommitTablesAsync(string entityId, string companyId, DatabaseTableCommitRequest request, string? modifiedBy, CancellationToken ct = default);

    // ---- Table freshness checks ----

    /// <summary>Gets a Table entity's freshness-check config (null when none configured).</summary>
    Task<TableCheckDto?> GetTableCheckAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Creates/updates the freshness-check config for a Table entity.</summary>
    Task<TableCheckDto> SaveTableCheckAsync(string entityId, string companyId, TableCheckRequest request, string? modifiedBy, CancellationToken ct = default);

    /// <summary>Removes a Table entity's freshness-check config.</summary>
    Task<bool> DeleteTableCheckAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Resolves the Table's parent Database connection and runs its freshness query now (for the UI "run now").</summary>
    Task<TableFreshnessResult> RunFreshnessCheckAsync(string entityId, string companyId, CancellationToken ct = default);

    // ---- Pure probes (no DbContext; safe to call concurrently from the ping job) ----

    /// <summary>Opens the (already-decrypted) connection read-only and runs SELECT 1, timing the round-trip.</summary>
    Task<DatabaseProbeResult> ProbeConnectionAsync(DatabaseConnection decryptedConnection, CancellationToken ct = default);

    /// <summary>Reads MAX(timestamp) + row count (read-only) for a table over the (already-decrypted) connection.</summary>
    Task<TableFreshnessResult> CheckFreshnessAsync(DatabaseConnection decryptedConnection, string tableFullName, string freshnessColumn, int maxAgeMinutes, CancellationToken ct = default);

    /// <summary>Loads and decrypts the connection of the Database entity a given Table entity depends on (null when none).</summary>
    Task<DatabaseConnection?> GetDecryptedParentConnectionAsync(string tableEntityId, string companyId, CancellationToken ct = default);

    /// <summary>Loads and decrypts a Database entity's own connection (null when none).</summary>
    Task<DatabaseConnection?> GetDecryptedConnectionAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Runs a read-only SELECT over the (already-decrypted) connection and streams the result
    /// to a UTF-8 CSV file (header + rows). Returns the number of data rows written. Used by scheduled
    /// ingestion to pull an external table/query into a dataset.</summary>
    Task<int> ReadToTempCsvAsync(DatabaseConnection decryptedConnection, string query, string destCsvPath, CancellationToken ct = default, int? commandTimeoutSeconds = null, IProgress<long>? rowProgress = null);

    /// <summary>Like <see cref="ReadToTempCsvAsync"/> but reads the query in ordered keyset pages of
    /// <paramref name="batchSize"/> rows (WHERE key &gt; lastKey ORDER BY key), appending to one CSV.
    /// Each page is a short, bounded query — avoids a single multi-million-row statement timing out.
    /// <paramref name="keyColumn"/> must be sortable and ideally unique. ADO providers only.</summary>
    Task<int> ReadToTempCsvBatchedAsync(DatabaseConnection decryptedConnection, string baseQuery, string keyColumn, int batchSize, string destCsvPath, CancellationToken ct = default, int? commandTimeoutSeconds = null, IProgress<long>? rowProgress = null);

    /// <summary>Pages a ClickHouse query (ORDER BY key + LIMIT/OFFSET) over the read-only HTTP CSV endpoint,
    /// invoking <paramref name="onPage"/> for each page as it is fetched. Each call receives the full page CSV
    /// (header + rows) and the page's row count, so callers can import batch-by-batch instead of buffering the
    /// whole result. <paramref name="keyColumn"/> may be a comma-separated combination that is collectively
    /// unique. Returns the total row count. ClickHouse only.</summary>
    Task<int> ReadClickHouseBatchesAsync(DatabaseConnection decryptedConnection, string baseQuery, string keyColumn, int batchSize, Func<string, int, Task> onPage, CancellationToken ct = default, int? commandTimeoutSeconds = null);
}

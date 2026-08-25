using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Every DuckDB operation a pipeline run needs. Implemented by <c>DuckdbService</c> as a second interface
/// on the same class rather than as a new service, and that is deliberate: the path resolution
/// (<c>ResolveDbPath</c> and its per-request cache), the identifier quoting, the target-column reader and
/// <c>PromoteRelationAsync</c> are all private to that class. A separate service would have to duplicate
/// them, and duplicated path resolution is exactly the drift the comments in that file warn about.
/// <para>
/// Two rules run through the whole implementation, both forced by DuckDB itself:
/// </para>
/// <list type="bullet">
/// <item><b>Connection per operation.</b> A DuckDB file allows one read-write handle at a time, and the web
/// app takes that handle merely by showing a table in the data viewer. Holding one for the length of a run
/// would block the UI for minutes, so nothing here keeps a connection open across calls.</item>
/// <item><b>Cross-file access is a short burst.</b> ATTACH read-only, copy, DETACH. Never a write that
/// spans two catalogs, because DuckDB cannot hold a transaction across attached databases and the
/// destination write must be atomic.</item>
/// </list>
/// </summary>
public interface IPipelineStore
{
    /// <summary>
    /// Materializes <paramref name="selectSql"/> as a table named <paramref name="relation"/> inside one
    /// dataset. This is how every transform step executes.
    /// </summary>
    Task<PipelineRelationResult> MaterializeAsync(
        string datasetId, string relation, string selectSql, int? timeoutSeconds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Copies a table out of another dataset's file into this one. Uses a brief read-only ATTACH, so it
    /// contends with a data-viewer tab on the source only for the duration of the copy.
    /// </summary>
    Task<PipelineRelationResult> MaterializeFromDatasetAsync(
        string datasetId, string relation, string sourceDatasetId, string sourceTable,
        IReadOnlyList<string>? columns = null, string? where = null, int? rowLimit = null,
        int? timeoutSeconds = null, CancellationToken ct = default);

    /// <summary>Loads a file (Excel / CSV / TSV / JSON / Parquet) into a relation.</summary>
    Task<PipelineRelationResult> MaterializeFromFileAsync(
        string datasetId, string relation, string filePath, ImportFileFormat format,
        bool hasHeader = true, string? sheet = null, int? rowLimit = null, int? timeoutSeconds = null,
        PipelineFileReadOptions? readOptions = null, bool addSourceFileColumn = false,
        string? badRowMode = null, CancellationToken ct = default);

    /// <summary>The columns of an existing relation, as DuckDB reports them.</summary>
    Task<List<PipelineColumn>> DescribeRelationAsync(
        string datasetId, string relation, CancellationToken ct = default);

    /// <summary>A few rows of a relation, for the step inspector and the mapping grid.</summary>
    Task<PipelinePreview> PreviewRelationAsync(
        string datasetId, string relation, int rows, CancellationToken ct = default);

    /// <summary>
    /// The destination write. Brings <paramref name="sourceRelation"/> into the target dataset's own file
    /// first, then appends / replaces / upserts entirely within that one catalog so the write is
    /// transactional.
    /// </summary>
    Task<ImportResult> WriteRelationToTableAsync(
        string sourceDatasetId, string sourceRelation,
        string targetDatasetId, string targetTable,
        ImportMode mode, List<string> keyColumns, bool createIfMissing,
        CancellationToken ct = default);

    /// <summary>
    /// Drops every table in a dataset whose name starts with <paramref name="prefix"/>. Used to clear a
    /// crashed run's leftovers out of a destination dataset.
    /// </summary>
    /// <summary>
    /// One value from a read-only query against a dataset. For the incremental ceiling — a
    /// <c>SELECT MAX(col)</c> that must not take the write handle, because the pipeline may be reading a
    /// dataset somebody is looking at.
    /// </summary>
    Task<PipelineScalarResult> ReadScalarAsync(
        string datasetId, string sql, int? timeoutSeconds = null, CancellationToken ct = default);

    Task<int> DropRelationsAsync(string datasetId, string prefix, CancellationToken ct = default);

    /// <summary>
    /// Streams a relation to a consumer as an open reader.
    /// <para>
    /// Callback-shaped rather than returning the reader, because the DuckDB connection has to outlive the
    /// read and this keeps its lifetime here rather than relying on every caller to dispose correctly. It is
    /// what lets the external writers hand a live reader straight to SqlBulkCopy or MySqlBulkCopy without
    /// ever materialising a row set in memory.
    /// </para>
    /// </summary>
    Task<T> ReadRelationAsync<T>(
        string datasetId,
        string relation,
        Func<System.Data.Common.DbDataReader, List<PipelineColumn>, CancellationToken, Task<T>> consume,
        CancellationToken ct = default);
}

/// <summary>
/// One scalar plus the DuckDB type it came back as. The type is carried because a watermark's type decides
/// how the next run quotes it.
/// </summary>
public sealed record PipelineScalarResult(bool Success, object? Value, string? TypeName, string? Error)
{
    public static PipelineScalarResult Ok(object? value, string? typeName) => new(true, value, typeName, null);
    public static PipelineScalarResult Fail(string error) => new(false, null, null, error);
}

/// <summary>The outcome of materializing a relation. Errors are returned, never thrown.</summary>
public sealed record PipelineRelationResult(
    bool Success,
    string? Error,
    string? ErrorType,
    long RowCount,
    List<PipelineColumn> Columns,
    /// <summary>The statement that ran. Surfaced on the step row — the most useful diagnostic there is.</summary>
    string? Sql,
    /// <summary>
    /// Rows the reader refused and skipped. Non-zero means this step succeeded WITHOUT all its data, which
    /// is exactly the state a run can otherwise report as a clean success.
    /// </summary>
    long RowsRejected = 0,
    /// <summary>Relation holding the rejected rows, when the step was set to quarantine them.</summary>
    string? RejectRelation = null)
{
    public static PipelineRelationResult Ok(long rows, List<PipelineColumn> columns, string sql) =>
        new(true, null, null, rows, columns, sql);

    public static PipelineRelationResult Ok(
        long rows, List<PipelineColumn> columns, string sql, long rejected, string? rejectRelation) =>
        new(true, null, null, rows, columns, sql, rejected, rejectRelation);

    public static PipelineRelationResult Fail(string error, string errorType, string? sql = null) =>
        new(false, error, errorType, 0, new(), sql);
}

/// <summary>A handful of rows plus their columns.</summary>
public sealed record PipelinePreview(
    List<PipelineColumn> Columns,
    List<Dictionary<string, object?>> Rows)
{
    public static PipelinePreview Empty => new(new(), new());
}

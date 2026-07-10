using Application.Shared.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using Application.Shared.Models.Data;

namespace Application.Shared.Services.Data;

public interface IDuckdbService
{
    Task CreateDatabaseAsync(Dataset dataset);
    /// <summary>True if the dataset's DuckDB file exists on disk.</summary>
    bool DatabaseExists(string datasetId);
    /// <summary>Creates the dataset's DuckDB file if it is missing (no-op if it already exists).</summary>
    Task EnsureDatabaseAsync(string datasetId);
    Task DeleteDatabaseAsync(Dataset dataset);
    Task UpdateDatabaseAsync(string datasetId, string updateQuery);
    Task<List<T>> ExecuteQueryAsync<T>(string databasePath, string query, Func<IDataReader, T> mapFunction);
    Task<List<T>> ExecuteQueryAsync<T>(Dataset dataset, string query, Func<IDataReader, T> mapFunction);
    Task<string> ExecuteQueryAsync(Dataset dataset, string query);
    Task<List<Column>> GetTableColumnsAsync(string datasetId, string tableName);
    Task<IEnumerable<string>> GetTablesAsync(string datasetId);

    // Storage stats for the datasets/tables list pages (computed on demand, never persisted).
    /// <summary>On-disk size of the dataset's DuckDB file in bytes; 0 if the file doesn't exist.</summary>
    long GetDatabaseFileSize(string datasetId);
    /// <summary>Table count + total estimated row count across all user tables (one cheap catalog query).</summary>
    Task<(int TableCount, long TotalRows)> GetDatasetTableSummaryAsync(string datasetId, System.Threading.CancellationToken ct = default);
    /// <summary>Per-table row count, column count and estimated on-disk (compressed) size in bytes.</summary>
    Task<List<TableStats>> GetTableStatsAsync(string datasetId, System.Threading.CancellationToken ct = default);
    Task<Table> GetTableAsync(string datasetId, string tableName);
    Task<Table> CreateTableAsync(Table table);
    Task<bool> DeleteTableAsync(string datasetId, string tableName);

    // Moves a table's data from one dataset's DuckDB file to another's, preserving column types exactly
    // (native DuckDB ATTACH-based copy, not a CSV round-trip). Errors are returned via MoveTableResult.Error.
    Task<MoveTableResult> MoveTableAsync(string sourceDatasetId, string tableName, string targetDatasetId, System.Threading.CancellationToken ct = default);
    Task<bool> ImportCsvDataAsync(string datasetId, string tableName, Stream csvStream);
    Task<bool> ImportCsvDataAsync(string companyId, string datasetId, string tableName, Stream csvStream, bool createDataset = false, bool createTable = false);

    // New methods for data querying
    Task<TableDataResult> QueryTableDataAsync(TableDataQuery query);
    Task<int> GetTableRowCountAsync(string datasetId, string tableName, List<FilterCondition>? filters = null);

    // Row-level editing for the data viewer. Rows are identified by the DuckDB rowid (returned in the
    // page when TableDataQuery.IncludeRowId is set). Each value is the raw string form of a column and is
    // CAST to the column's type. Errors are returned via RowMutationResult.Error, never thrown.
    Task<RowMutationResult> UpdateRowAsync(string datasetId, string tableName, long rowId, Dictionary<string, string?> values, System.Threading.CancellationToken ct = default);
    Task<RowMutationResult> InsertRowAsync(string datasetId, string tableName, Dictionary<string, string?> values, System.Threading.CancellationToken ct = default);
    Task<RowMutationResult> DeleteRowAsync(string datasetId, string tableName, long rowId, System.Threading.CancellationToken ct = default);

    // Applies a batch of inserts/updates/deletes (from the spreadsheet editor) atomically in one
    // transaction. Errors are returned via BulkRowEditResult.Error, never thrown.
    Task<BulkRowEditResult> ApplyRowChangesAsync(string datasetId, string tableName, BulkRowEditRequest changes, System.Threading.CancellationToken ct = default);

    // Ad-hoc SQL workbench. Reads open a read-only DuckDB handle; writes (allowWrite) open a
    // read-write handle. SQL errors are returned via SqlQueryResult.Error, never thrown.
    Task<SqlQueryResult> ExecuteSqlAsync(string datasetId, string sql, bool allowWrite, int maxRows, System.Threading.CancellationToken ct = default);

    // Write-back: materialize a SELECT query as a new table or view in the dataset.
    Task<SqlQueryResult> CreateObjectFromQueryAsync(string datasetId, string objectName, string sql, bool asView, System.Threading.CancellationToken ct = default);

    // Re-runs a SELECT against the dataset's own DuckDB tables and refreshes a target table in place
    // (the SqlQuery ingestion kind). Errors are returned via ImportResult.Error, never thrown.
    Task<ImportResult> RunQueryIntoTableAsync(string datasetId, string tableName, string sql, ImportMode mode, List<string> keyColumns, bool createIfMissing, System.Threading.CancellationToken ct = default);

    // Stage a file into a temporary table and validate it against the target table's schema
    // (type-cast checks, missing/extra columns, preview rows) without committing anything.
    // Errors are returned via ImportValidationResult.Error, never thrown.
    Task<ImportValidationResult> ValidateImportAsync(string datasetId, string tableName, Stream fileStream, ImportFileFormat format, System.Threading.CancellationToken ct = default);

    // Like ValidateImportAsync but validates against a caller-supplied schema (the columns being defined
    // in the import wizard) rather than an existing table — true pre-commit validation for new tables.
    Task<ImportValidationResult> ValidateImportAgainstSchemaAsync(string datasetId, List<Column> targetColumns, Stream fileStream, ImportFileFormat format, System.Threading.CancellationToken ct = default);

    // Stage a file (no target table) and return the columns DuckDB infers plus a preview — lets the
    // wizard build a schema editor for formats the browser can't parse (JSON/Parquet/Excel).
    Task<FilePeekResult> PeekFileAsync(string datasetId, Stream fileStream, ImportFileFormat format, System.Threading.CancellationToken ct = default);

    // Stage a file and commit it into the target table with the chosen mode
    // (append / replace / upsert on keyColumns). Optionally skips rows that fail TRY_CAST.
    // Errors are returned via ImportResult.Error, never thrown.
    Task<ImportResult> ImportFileAsync(string datasetId, string tableName, Stream fileStream, ImportFileFormat format, ImportMode mode, List<string> keyColumns, bool skipInvalidRows, bool createIfMissing = false, System.Threading.CancellationToken ct = default);
}

using System.Collections.Generic;

namespace Application.Shared.Models.Data;

/// <summary>Supported source file formats for an import. Routes the DuckDB reader function.</summary>
public enum ImportFileFormat
{
    Csv,
    Tsv,
    Json,
    Parquet,
    Excel
}

/// <summary>How rows from the file are written into the target table.</summary>
public enum ImportMode
{
    // Add the file's rows to whatever is already in the table.
    Append,
    // Empty the table first, then load the file's rows.
    Replace,
    // Delete target rows whose key columns match the file, then insert the file's rows
    // (dedupe on user-picked key columns).
    Upsert
}

/// <summary>
/// Outcome of validating a file against a target table's schema, before committing.
/// Data/SQL errors are reported via <see cref="Error"/> (never thrown).
/// </summary>
public class ImportValidationResult
{
    public int TotalRows { get; set; }
    // Columns found in the file/staging set.
    public List<string> FileColumns { get; set; } = new();
    // Target-schema columns absent from the file.
    public List<string> MissingColumns { get; set; } = new();
    // File columns absent from the target schema (ignored on import).
    public List<string> ExtraColumns { get; set; } = new();
    // Per target column present in the file: how many staged values fail TRY_CAST to the target type.
    public List<ColumnValidation> ColumnValidations { get; set; } = new();
    // First N staged rows for preview (run through the same value normalization as the viewer).
    public List<Dictionary<string, object?>> PreviewRows { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>Per-column cast-validation summary for a staged file.</summary>
public class ColumnValidation
{
    public string Column { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public int InvalidCount { get; set; }
    public List<string> SampleInvalidValues { get; set; } = new();
}

/// <summary>
/// Columns + preview produced by staging a file without a target table — used by the import wizard
/// to populate the schema editor for formats the browser can't parse (JSON/Parquet/Excel).
/// </summary>
public class FilePeekResult
{
    public List<Column> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> PreviewRows { get; set; } = new();
    public int TotalRows { get; set; }
    public string? Error { get; set; }
}

/// <summary>Outcome of committing an import. Errors are reported via <see cref="Error"/> (never thrown).</summary>
public class ImportResult
{
    public bool Success { get; set; }
    public int RowsInserted { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsSkipped { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Machine-readable cause, for callers that need to tell a transient failure from a real one. Added for
    /// ETL pipelines, which must distinguish "the dataset is momentarily in use" (retry) from "the columns
    /// do not match" (fix the pipeline). Left null by the file-import paths, which only surface Error.
    /// </summary>
    public string? ErrorType { get; set; }
}

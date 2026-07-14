using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Data;

/// <summary>
/// AI-generated + human-edited semantic metadata for a single column of a dataset table (the
/// "semantic layer"). Keyed by (dataset_id, table_name, column_name, is_snapshot), scoped to a company.
/// Columns themselves are not persisted — they're read live from DuckDB / discovered from the source — so
/// this row is a documentation overlay that a column may or may not have. Persisted via the DACPAC table
/// <c>dataset_column_doc</c> (schema managed as SQL, not EF migrations); PascalCase auto-maps to
/// snake_case by <see cref="Data.ApplicationDbContext"/>.
/// </summary>
public class DatasetColumnDoc
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string CompanyId { get; set; } = string.Empty;

    public string DatasetId { get; set; } = string.Empty;

    [StringLength(150)]
    public string TableName { get; set; } = string.Empty;

    [StringLength(150)]
    public string ColumnName { get; set; } = string.Empty;

    /// <summary>True = documents the dataset's DuckDB snapshot (Local datasets, and External datasets in
    /// snapshot mode); false = documents the External dataset's live source table. Part of the logical key
    /// so source and snapshot docs don't collide.</summary>
    public bool IsSnapshot { get; set; } = true;

    /// <summary>Friendly display label, e.g. "Net Amount" for <c>net_amt_acy</c>.</summary>
    [StringLength(200)]
    public string? DisplayName { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>Semantic type/role, e.g. currency, percentage, date, email, identifier, quantity, category.</summary>
    [StringLength(60)]
    public string? SemanticType { get; set; }

    [StringLength(60)]
    public string? Unit { get; set; }

    /// <summary>Advisory PII flag — surfaced for human review, never used to enforce access.</summary>
    public bool IsPii { get; set; }

    [StringLength(60)]
    public string? PiiType { get; set; }

    /// <summary>True when last written by the AI generator; cleared to false on a manual edit.</summary>
    public bool IsAiGenerated { get; set; }

    public DateTime? GeneratedAt { get; set; }

    public string? EditedBy { get; set; }

    public DateTime? ModifiedAt { get; set; }
}

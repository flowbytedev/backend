using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Notebooks;

/// <summary>One cell of a <see cref="QueryNotebook"/> — either a SQL cell or a Markdown note.</summary>
public class QueryNotebookCell
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string NotebookId { get; set; } = string.Empty;

    public string CompanyId { get; set; } = string.Empty;

    /// <summary>The dataset this cell runs against. Null for markdown cells.</summary>
    public string? DatasetId { get; set; }

    /// <summary>"sql" or "markdown".</summary>
    [StringLength(20)]
    public string CellType { get; set; } = "sql";

    /// <summary>
    /// User-editable reference token (e.g. "orders_by_region"). Once the cell successfully materializes,
    /// this is also the DuckDB object name a later cell can select from (after namespacing — see
    /// <c>QueryNotebookService</c>).
    /// </summary>
    [StringLength(100)]
    public string? Name { get; set; }

    public string? SqlText { get; set; }

    public string? MarkdownText { get; set; }

    /// <summary>JSON array of other cell ids this cell depends on (drives run order + cross-dataset sync).</summary>
    public string? ReferencedCellIds { get; set; }

    /// <summary>For External datasets: query the live source (false) or the local snapshot (true) — mirrors
    /// the Query Workbench's Source/Snapshot toggle.</summary>
    public bool SnapshotMode { get; set; }

    public int SortOrder { get; set; }

    /// <summary>"success" | "error" | null (never run).</summary>
    [StringLength(20)]
    public string? LastRunStatus { get; set; }

    public string? LastRunError { get; set; }

    /// <summary>The actual DuckDB table/view name once successfully materialized; null otherwise.</summary>
    [StringLength(150)]
    public string? LastMaterializedObject { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
}

using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Notebooks;

/// <summary>One historical execution of a <see cref="QueryNotebookCell"/> — <c>QueryNotebookCell</c> itself
/// only ever tracks its LATEST run; this is the append-only log behind the "run history" panel.</summary>
public class NotebookCellRun
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string NotebookId { get; set; } = string.Empty;
    public string CellId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;

    /// <summary>"success" | "error".</summary>
    [StringLength(20)]
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int? RowsReturned { get; set; }
    public long? ElapsedMs { get; set; }
    [StringLength(150)]
    public string? MaterializedObject { get; set; }

    /// <summary>"manual" | "run_all" | "scheduled".</summary>
    [StringLength(20)]
    public string TriggeredBy { get; set; } = "manual";

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Notebooks;

/// <summary>
/// A MotherDuck-style SQL notebook: a named, ordered sequence of cells (<see cref="QueryNotebookCell"/>),
/// scoped to a company. Unlike a dataset-bound page, a notebook itself is not tied to one dataset — each
/// cell picks its own. Persisted via the DACPAC table <c>query_notebook</c> (schema managed as SQL, not EF
/// migrations).
/// </summary>
public class QueryNotebook
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string CompanyId { get; set; } = string.Empty;

    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public bool IsShared { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }

    /// <summary>Standard 5-field cron. Null/empty means no recurring schedule.</summary>
    [StringLength(100)]
    public string? CronExpression { get; set; }
    public bool ScheduleEnabled { get; set; }
    /// <summary>IANA id (e.g. "Asia/Beirut"); falls back to the scheduler's default when null.</summary>
    [StringLength(100)]
    public string? ScheduleTimeZone { get; set; }
    public DateTime? LastScheduledRunAt { get; set; }
    /// <summary>"success" | "error" | null (never run on a schedule).</summary>
    [StringLength(20)]
    public string? LastScheduledRunStatus { get; set; }
    public string? LastScheduledRunError { get; set; }
}

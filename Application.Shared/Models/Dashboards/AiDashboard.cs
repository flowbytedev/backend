using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Dashboards;

/// <summary>
/// A conversationally-built dashboard: a named collection of widgets bound to a single dataset,
/// scoped to a company. Widgets are stored in <see cref="AiDashboardWidget"/>. Persisted via the
/// DACPAC table <c>ai_dashboard</c> (schema managed as SQL, not EF migrations).
/// </summary>
public class AiDashboard
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string CompanyId { get; set; } = string.Empty;

    public string DatasetId { get; set; } = string.Empty;

    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

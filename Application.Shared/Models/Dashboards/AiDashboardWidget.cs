using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Dashboards;

/// <summary>
/// One widget on an <see cref="AiDashboard"/>: a stored read-only SELECT against the dashboard's
/// dataset, plus how to visualize it. <see cref="ConfigJson"/> holds a serialized
/// <see cref="Application.Shared.Models.Dashboards.WidgetConfig"/> (field mappings). Persisted via
/// the DACPAC table <c>ai_dashboard_widget</c>.
/// </summary>
public class AiDashboardWidget
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string DashboardId { get; set; } = string.Empty;

    public string CompanyId { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    // One of: table | bar | line | kpi.
    [Required]
    [StringLength(30)]
    public string VizType { get; set; } = "table";

    // The read-only SELECT this widget renders (validated before persistence).
    [Required]
    public string SqlText { get; set; } = string.Empty;

    // Serialized WidgetConfig — field mappings (x/series/value) and formatting hints.
    public string? ConfigJson { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

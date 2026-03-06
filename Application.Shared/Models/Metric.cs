using Application.Shared.Enums;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models;

public class Metric : BaseModel
{
    public int Id { get; set; }

    // Navigation properties for one-to-many relationships
    public ICollection<MetricFunction> Functions { get; set; } = new List<MetricFunction>();
    public ICollection<MetricOwner> Owners { get; set; } = new List<MetricOwner>();
    public ICollection<MetricRecipient> Recipients { get; set; } = new List<MetricRecipient>();
    public ICollection<MetricVerifier> Verifiers { get; set; } = new List<MetricVerifier>();
    public ICollection<MetricDimension> Dimensions { get; set; } = new List<MetricDimension>();
    public ICollection<MetricFilter> Filters { get; set; } = new List<MetricFilter>();

    // Foreign key for data source (many-to-one: many metrics can share one data source)
    public int? MetricDataSourceId { get; set; }
    public MetricDataSource? MetricDataSource { get; set; }

    [MaxLength(200)]
    public string? ContactEmail { get; set; }

    [MaxLength(50)]
    public string? ContactNumber { get; set; }

    [Required]
    [MaxLength(300)]
    public string KeyPerformanceArea { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string KPI { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Formula { get; set; }

    [MaxLength(5000)]
    public string? Query { get; set; }

    [Required]
    public MetricType Type { get; set; }

    [Required]
    public MetricPerspective Perspective { get; set; }

    [Required]
    public MetricLevel KPILevel { get; set; }

    [MaxLength(200)]
    public string? Target { get; set; }

    [MaxLength(1000)]
    public string? UnintendedConsequences { get; set; }

    [MaxLength(1000)]
    public string? MitigatingFactors { get; set; }

    [MaxLength(100)]
    public string? UnitOfMeasure { get; set; }

    [MaxLength(2000)]
    public string? KPIControls { get; set; }

    public ReportingFrequency? DataCapture { get; set; }

    public ReportingFrequency? DataReporting { get; set; }

    [Required]
    public MetricPolarity Polarity { get; set; }

    [MaxLength(300)]
    public string? DataSource { get; set; }

    public DataIntegrityLevel? DataIntegrity { get; set; }

    public DateTime? RevisionDate { get; set; }

    public bool DataReady { get; set; }

    [MaxLength(200)]
    public string? Report { get; set; }

    [MaxLength(2000)]
    public string? Comment { get; set; }

    public bool IsActive { get; set; } = true;
}

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

public class MetricValue : BaseModel
{
    public int Id { get; set; }

    [Required]
    public int MetricId { get; set; }

    [Required]
    public DateTime PeriodDate { get; set; }

    public decimal? NumericValue { get; set; }

    [MaxLength(1000)]
    public string? TextValue { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public bool IsValidated { get; set; }

    [MaxLength(200)]
    public string? ValidatedBy { get; set; }

    public DateTime? ValidatedDate { get; set; }

    // Navigation property
    [JsonIgnore]
    public Metric? Metric { get; set; }
}

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

public class MetricTarget : BaseModel
{
    public int Id { get; set; }

    [Required]
    public int MetricId { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public decimal? MinTarget { get; set; }

    public decimal? MaxTarget { get; set; }

    public decimal? OptimalTarget { get; set; }

    [MaxLength(500)]
    public string? TargetDescription { get; set; }

    [MaxLength(200)]
    public string? SetBy { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation property
    [JsonIgnore]
    public Metric? Metric { get; set; }
}

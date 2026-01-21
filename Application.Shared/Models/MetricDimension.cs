using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

public class MetricDimension : BaseModel
{
    public int Id { get; set; }

    public int MetricId { get; set; }

    [JsonIgnore]
    public Metric? Metric { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? SourceTable { get; set; }

    [MaxLength(200)]
    public string? SourceColumn { get; set; }
}

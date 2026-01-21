using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

public class MetricFunction : BaseModel
{
    public int Id { get; set; }

    public int MetricId { get; set; }

    [JsonIgnore]
    public Metric? Metric { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Function { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? SubFunction { get; set; }

    [MaxLength(200)]
    public string? FunctionHead { get; set; }
}

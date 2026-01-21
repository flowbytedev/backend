using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

public class MetricOwner : BaseModel
{
    public int Id { get; set; }

    public int MetricId { get; set; }

    [JsonIgnore]
    public Metric? Metric { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string OwnerName { get; set; } = string.Empty;
}

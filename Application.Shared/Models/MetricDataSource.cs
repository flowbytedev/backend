using Application.Shared.Enums;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

public class MetricDataSource : BaseModel
{
    public int Id { get; set; }

    // Navigation property for metrics using this data source (many-to-one)
    [JsonIgnore]
    public ICollection<Metric> Metrics { get; set; } = new List<Metric>();

    [Required]
    public DataSourceType Type { get; set; }

    [Required]
    [MaxLength(200)]
    public string Host { get; set; } = string.Empty;

    [Required]
    public int Port { get; set; }

    [Required]
    [MaxLength(100)]
    public string Database { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Password { get; set; }

    [MaxLength(200)]
    public string? ConnectionName { get; set; }

    public bool UseSSL { get; set; } = false;
}

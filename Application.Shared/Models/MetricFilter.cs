using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

public class MetricFilter : BaseModel
{
    public int Id { get; set; }

    public int MetricId { get; set; }

    [JsonIgnore]
    public Metric? Metric { get; set; }

    [Required]
    [MaxLength(100)]
    public string ColumnName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string FilterLabel { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string FilterType { get; set; } = "text"; // text, date, number, select

    [Required]
    [MaxLength(20)]
    public string Operator { get; set; } = "="; // =, <>, <, >, <=, >=, IN, NOT IN, LIKE, NOT LIKE, BETWEEN

    [MaxLength(1000)]
    public string? DefaultValue { get; set; }

    [MaxLength(2000)]
    public string? SelectOptions { get; set; } // For select type, semicolon-separated options

    public bool IsRequired { get; set; } = false;

    public int SortOrder { get; set; } = 0;

    [MaxLength(500)]
    public string? Placeholder { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }
}

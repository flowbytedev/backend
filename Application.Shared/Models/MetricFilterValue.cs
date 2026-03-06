using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models;

/// <summary>
/// Represents filter values passed when executing a metric query
/// </summary>
public class MetricFilterValue
{
    [Required]
    public string ColumnName { get; set; } = string.Empty;

    public string? Value { get; set; }
}

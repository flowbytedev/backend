namespace Application.Shared.Models.Metrics;

/// <summary>
/// A single KPI card for the metrics dashboard, derived from the warehouse <c>org.kpi</c> table.
/// The current period is the most recent (year, month) row for a given (department, kpi); the
/// comparison values are the prior calendar month and the same month one year earlier.
/// </summary>
public class KpiCardDto
{
    public string Department { get; set; } = string.Empty;
    public string Kpi { get; set; } = string.Empty;

    /// <summary>Inferred display unit: <c>%</c>, <c>days</c>, or <c>num</c>.</summary>
    public string Unit { get; set; } = "num";

    /// <summary>Inferred optimization direction: <c>up</c> (higher is better) or <c>down</c> (lower is better).</summary>
    public string Direction { get; set; } = "up";

    public double Current { get; set; }
    public double Target { get; set; }

    /// <summary>Value for the prior calendar month, if present in the series.</summary>
    public double? PrevMonth { get; set; }

    /// <summary>Value for the same month one year earlier, if present in the series.</summary>
    public double? PrevYear { get; set; }

    /// <summary>Year of the current (latest) period.</summary>
    public int Year { get; set; }

    /// <summary>Month label of the current (latest) period (e.g. <c>Jun</c>).</summary>
    public string Month { get; set; } = string.Empty;
}

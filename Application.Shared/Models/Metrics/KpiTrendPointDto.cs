namespace Application.Shared.Models.Metrics;

/// <summary>
/// A single point on a KPI trend chart: one month's value and target. The trend window is the
/// current year to date plus the immediately preceding month (December of the prior year).
/// </summary>
public class KpiTrendPointDto
{
    public int Year { get; set; }
    public int MonthNum { get; set; }

    /// <summary>Month label of the point (e.g. <c>Jun</c>).</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Short axis label including the 2-digit year (e.g. <c>Jun '26</c>).</summary>
    public string Label { get; set; } = string.Empty;

    public double Value { get; set; }
    public double Target { get; set; }
}

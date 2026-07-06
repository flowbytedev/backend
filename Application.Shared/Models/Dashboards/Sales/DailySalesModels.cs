namespace Application.Shared.Models.Dashboards.Sales;

/// <summary>Sales + transaction figures for one window (current / last-month / last-year).</summary>
public class DailySalesFigures
{
    public double Sales { get; set; }
    public long Transactions { get; set; }
}

/// <summary>
/// One row of the Daily Sales table at a given level of the hierarchy
/// (store scheme → store → division → category).
///
/// <para><see cref="Value"/> is what's <b>displayed</b> — it depends only on the scope
/// (Yesterday → the day, MTD → month-to-date, YTD → year-to-date) and never changes with the
/// Day/To-Date toggle.</para>
///
/// <para>The <c>Cmp*</c> figures are the <b>comparison basis</b> the vs-LM / vs-LY percentages are
/// computed from — the toggle changes only these: Day = day-over-day, To-Date = period-over-period.
/// The percentage is <c>(CmpCurrent − CmpLastMonth) / CmpLastMonth</c> (and likewise for last year).</para>
/// </summary>
public class DailySalesRow
{
    /// <summary>Hierarchy level: <c>scheme</c>, <c>store</c>, <c>division</c> or <c>category</c>.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Stable key used to drill into children (scheme name / store code / division code / category code).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Whether this row can be expanded to a deeper level.</summary>
    public bool HasChildren { get; set; }

    /// <summary>Displayed figures — scope window only (independent of the Day/To-Date toggle).</summary>
    public DailySalesFigures Value { get; set; } = new();

    /// <summary>Comparison-basis figures for the current window (Day or To-Date).</summary>
    public DailySalesFigures CmpCurrent { get; set; } = new();

    /// <summary>Comparison-basis figures one month back.</summary>
    public DailySalesFigures CmpLastMonth { get; set; } = new();

    /// <summary>Comparison-basis figures one year back.</summary>
    public DailySalesFigures CmpLastYear { get; set; } = new();
}

/// <summary>
/// Payload for the Daily Sales dashboard. A single response covers one level of the drill-down:
/// the initial <c>scheme</c>-level request also carries <see cref="Totals"/> for the top KPI cards;
/// deeper expand requests return only <see cref="Rows"/>.
/// </summary>
public class DailySalesResponse
{
    /// <summary>False when the SalesDataset ClickHouse connection isn't configured.</summary>
    public bool Configured { get; set; }
    public string? DataSourceName { get; set; }

    /// <summary>The anchor (as-of) date the windows are computed from.</summary>
    public DateTime Date { get; set; }

    /// <summary>MTD or YTD.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Day or ToDate.</summary>
    public string View { get; set; } = string.Empty;

    /// <summary>Human labels for the three windows (for the subtitle).</summary>
    public string CurrentLabel { get; set; } = string.Empty;
    public string LastMonthLabel { get; set; } = string.Empty;
    public string LastYearLabel { get; set; } = string.Empty;

    /// <summary>Grand totals across all stores — present only on the scheme-level (initial) response.</summary>
    public DailySalesRow? Totals { get; set; }

    /// <summary>Rows for the requested level.</summary>
    public List<DailySalesRow> Rows { get; set; } = new();
}

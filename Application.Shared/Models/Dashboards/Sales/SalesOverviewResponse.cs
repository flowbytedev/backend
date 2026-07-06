namespace Application.Shared.Models.Dashboards.Sales;

/// <summary>
/// Full payload for the Sales Performance Overview dashboard. Every figure is aggregated
/// server-side from ClickHouse <c>sales_line</c> (joined to <c>store</c>, <c>item_details</c>,
/// <c>product_hierarchy</c>) and <c>sales_header_details</c> over the selected day window, so the
/// browser only renders a few dozen pre-aggregated points. All money is in accounting currency (acy).
/// </summary>
public class SalesOverviewResponse
{
    /// <summary>False when the company has no ClickHouse data source configured — the page shows a hint.</summary>
    public bool Configured { get; set; }

    /// <summary>Name of the ClickHouse data source the figures were read from (for display).</summary>
    public string? DataSourceName { get; set; }

    /// <summary>Inclusive start of the reporting window.</summary>
    public DateTime FromDate { get; set; }

    /// <summary>Inclusive end of the reporting window.</summary>
    public DateTime ToDate { get; set; }

    // ----- headline KPIs (current window) -----
    public double TotalRevenue { get; set; }
    public double TotalUnits { get; set; }
    public long TotalBaskets { get; set; }
    public double TotalDiscount { get; set; }

    /// <summary>Average basket value = revenue / baskets.</summary>
    public double AvgBasketValue { get; set; }

    // ----- prior equal-length window (for period-over-period deltas) -----
    public double PrevRevenue { get; set; }
    public double PrevUnits { get; set; }
    public long PrevBaskets { get; set; }

    // ----- breakdowns -----

    /// <summary>Daily revenue / units / baskets over the window.</summary>
    public List<SalesTrendPoint> Trend { get; set; } = new();

    /// <summary>Revenue &amp; units by store, ranked (top N).</summary>
    public List<SalesDimensionSlice> ByStore { get; set; } = new();

    /// <summary>Revenue &amp; units by product category, ranked (top N).</summary>
    public List<SalesDimensionSlice> ByCategory { get; set; } = new();

    /// <summary>Revenue &amp; units by hour of day (0–23).</summary>
    public List<SalesHourPoint> ByHour { get; set; } = new();

    /// <summary>Order count by payment method.</summary>
    public List<SalesPaymentSlice> ByPayment { get; set; } = new();

    /// <summary>Best-selling items by revenue (top N).</summary>
    public List<SalesItemRow> TopItems { get; set; } = new();
}

/// <summary>One day on the sales trend.</summary>
public class SalesTrendPoint
{
    /// <summary>Day, formatted <c>yyyy-MM-dd</c> as returned by ClickHouse <c>toDate</c>.</summary>
    public string Day { get; set; } = string.Empty;
    public double Revenue { get; set; }
    public double Units { get; set; }
    public long Baskets { get; set; }
}

/// <summary>A ranked slice of revenue by an arbitrary dimension (store, category).</summary>
public class SalesDimensionSlice
{
    /// <summary>Display name of the dimension member.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Underlying code (store code / category code) — used as a stable key / fallback label.</summary>
    public string Code { get; set; } = string.Empty;

    public double Revenue { get; set; }
    public double Units { get; set; }
}

/// <summary>Revenue / units for a single hour of the day.</summary>
public class SalesHourPoint
{
    public int Hour { get; set; }
    public double Revenue { get; set; }
    public double Units { get; set; }
}

/// <summary>Order count for a payment method.</summary>
public class SalesPaymentSlice
{
    public string Method { get; set; } = string.Empty;
    public long Orders { get; set; }
}

/// <summary>A top-selling item row.</summary>
public class SalesItemRow
{
    public string ItemNo { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Units { get; set; }
    public double Revenue { get; set; }
}

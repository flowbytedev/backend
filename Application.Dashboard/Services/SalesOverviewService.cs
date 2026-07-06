using System.Globalization;
using System.Text.RegularExpressions;
using Application.Dashboard.Configuration;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Dashboards.Sales;
using Application.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Dashboard.Services;

/// <summary>
/// Builds the Sales Performance Overview by aggregating <b>live</b> against the shared ClickHouse
/// <c>sales_dataset</c> warehouse (bound from the <c>SalesDataset</c> appsettings section, same model as
/// Daily Inventory). All heavy lifting runs as GROUP BY aggregations in ClickHouse — OLAP's sweet spot —
/// so each query returns only a handful of pre-aggregated rows. Unlike the OOS dashboard (which reads a
/// pre-ingested DuckDB table), this needs no ingestion step: it queries <c>sales_line</c> /
/// <c>sales_header_details</c> directly, scoped by <c>company_id</c>.
/// </summary>
public class SalesOverviewService : ISalesOverviewService
{
    private readonly IClickHouseService _clickHouse;
    private readonly SalesDatasetSettings _settings;
    private readonly ILogger<SalesOverviewService> _logger;

    // company_id arrives from the X-Company-Id header; validate before interpolating into SQL.
    private static readonly Regex SafeToken = new("^[A-Za-z0-9_\\-]+$", RegexOptions.Compiled);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SalesOverviewService(
        IClickHouseService clickHouse,
        IOptions<SalesDatasetSettings> settings,
        ILogger<SalesOverviewService> logger)
    {
        _clickHouse = clickHouse;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<SalesOverviewResponse> GetAsync(string companyId, int days, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId) || !SafeToken.IsMatch(companyId))
            throw new ArgumentException("Invalid company id.", nameof(companyId));

        days = Math.Clamp(days, 1, 365);

        var to = DateTime.Today;
        var from = to.AddDays(-(days - 1));
        var prevTo = from.AddDays(-1);
        var prevFrom = from.AddDays(-days);

        var response = new SalesOverviewResponse { FromDate = from, ToDate = to };

        // Single shared connection to the sales_dataset warehouse (SalesDataset appsettings section).
        // Not configured ⇒ nothing to show yet.
        if (string.IsNullOrWhiteSpace(_settings.Host) || string.IsNullOrWhiteSpace(_settings.Database))
            return response; // Configured stays false

        var source = new MetricDataSource
        {
            Type = DataSourceType.ClickHouse,
            Host = _settings.Host,
            Port = _settings.Port,
            Database = _settings.Database,
            Username = _settings.Username,
            Password = _settings.Password,
            UseSSL = _settings.UseSSL
        };

        response.Configured = true;
        response.DataSourceName = _settings.Database;

        var co = companyId.Replace("'", "''");
        var f = from.ToString("yyyy-MM-dd", Inv);
        var t = to.ToString("yyyy-MM-dd", Inv);
        var pf = prevFrom.ToString("yyyy-MM-dd", Inv);
        var pt = prevTo.ToString("yyyy-MM-dd", Inv);

        const string basket = "(store_code, pos_number, transaction_no, receipt_no)";

        try
        {
            // 1) Headline totals for the current window.
            var totals = await QueryAsync(source, $@"
                SELECT sum(net_amount_acy)     AS revenue,
                       sum(quantity)           AS units,
                       uniqExact({basket})     AS baskets,
                       sum(total_discount_acy) AS discount
                FROM sales_line
                WHERE company_id = '{co}' AND toDate(date) BETWEEN '{f}' AND '{t}'");
            if (totals.Count > 0)
            {
                var r = totals[0];
                response.TotalRevenue = GetDouble(r, "revenue");
                response.TotalUnits = GetDouble(r, "units");
                response.TotalBaskets = GetLong(r, "baskets");
                response.TotalDiscount = GetDouble(r, "discount");
                response.AvgBasketValue = response.TotalBaskets > 0
                    ? response.TotalRevenue / response.TotalBaskets : 0;
            }

            // 2) Prior equal-length window totals (for deltas).
            var prev = await QueryAsync(source, $@"
                SELECT sum(net_amount_acy) AS revenue,
                       sum(quantity)       AS units,
                       uniqExact({basket}) AS baskets
                FROM sales_line
                WHERE company_id = '{co}' AND toDate(date) BETWEEN '{pf}' AND '{pt}'");
            if (prev.Count > 0)
            {
                var r = prev[0];
                response.PrevRevenue = GetDouble(r, "revenue");
                response.PrevUnits = GetDouble(r, "units");
                response.PrevBaskets = GetLong(r, "baskets");
            }

            // 3) Daily trend.
            var trend = await QueryAsync(source, $@"
                SELECT toDate(date)        AS day,
                       sum(net_amount_acy) AS revenue,
                       sum(quantity)       AS units,
                       uniqExact({basket}) AS baskets
                FROM sales_line
                WHERE company_id = '{co}' AND toDate(date) BETWEEN '{f}' AND '{t}'
                GROUP BY day ORDER BY day");
            foreach (var r in trend)
                response.Trend.Add(new SalesTrendPoint
                {
                    Day = GetString(r, "day"),
                    Revenue = GetDouble(r, "revenue"),
                    Units = GetDouble(r, "units"),
                    Baskets = GetLong(r, "baskets")
                });

            // 4) Revenue by store (top 20).
            var byStore = await QueryAsync(source, $@"
                SELECT l.store_code        AS code,
                       any(s.store)        AS name,
                       sum(l.net_amount_acy) AS revenue,
                       sum(l.quantity)       AS units
                FROM sales_line AS l
                LEFT JOIN store AS s ON s.company_id = l.company_id AND s.code = l.store_code
                WHERE l.company_id = '{co}' AND toDate(l.date) BETWEEN '{f}' AND '{t}'
                GROUP BY l.store_code ORDER BY revenue DESC LIMIT 20");
            foreach (var r in byStore)
            {
                var code = GetString(r, "code");
                var name = GetString(r, "name");
                response.ByStore.Add(new SalesDimensionSlice
                {
                    Code = code,
                    Name = string.IsNullOrWhiteSpace(name) ? code : name,
                    Revenue = GetDouble(r, "revenue"),
                    Units = GetDouble(r, "units")
                });
            }

            // 5) Revenue by product category (top 12).
            var byCat = await QueryAsync(source, $@"
                SELECT coalesce(nullIf(ph.category, ''), nullIf(i.category_code, ''), 'Unknown') AS category,
                       sum(l.net_amount_acy) AS revenue,
                       sum(l.quantity)       AS units
                FROM sales_line AS l
                LEFT JOIN item_details AS i
                       ON i.company_id = l.company_id AND i.item_no = l.item_no AND i.variant_code = l.variant_code
                LEFT JOIN product_hierarchy AS ph
                       ON ph.company_id = i.company_id AND ph.category_code = i.category_code
                WHERE l.company_id = '{co}' AND toDate(l.date) BETWEEN '{f}' AND '{t}'
                GROUP BY category ORDER BY revenue DESC LIMIT 12");
            foreach (var r in byCat)
                response.ByCategory.Add(new SalesDimensionSlice
                {
                    Name = GetString(r, "category"),
                    Code = GetString(r, "category"),
                    Revenue = GetDouble(r, "revenue"),
                    Units = GetDouble(r, "units")
                });

            // 6) Revenue by hour of day.
            var byHour = await QueryAsync(source, $@"
                SELECT toHour(time)       AS hour,
                       sum(net_amount_acy) AS revenue,
                       sum(quantity)       AS units
                FROM sales_line
                WHERE company_id = '{co}' AND toDate(date) BETWEEN '{f}' AND '{t}'
                GROUP BY hour ORDER BY hour");
            foreach (var r in byHour)
                response.ByHour.Add(new SalesHourPoint
                {
                    Hour = (int)GetLong(r, "hour"),
                    Revenue = GetDouble(r, "revenue"),
                    Units = GetDouble(r, "units")
                });

            // 7) Payment method mix (from order headers).
            var byPay = await QueryAsync(source, $@"
                SELECT coalesce(nullIf(payment_method, ''), 'Unknown') AS method,
                       count() AS orders
                FROM sales_header_details
                WHERE company_id = '{co}' AND toDate(date) BETWEEN '{f}' AND '{t}'
                GROUP BY method ORDER BY orders DESC LIMIT 12");
            foreach (var r in byPay)
                response.ByPayment.Add(new SalesPaymentSlice
                {
                    Method = GetString(r, "method"),
                    Orders = GetLong(r, "orders")
                });

            // 8) Top-selling items (top 15).
            var topItems = await QueryAsync(source, $@"
                SELECT l.item_no          AS item_no,
                       any(i.description) AS description,
                       sum(l.quantity)      AS units,
                       sum(l.net_amount_acy) AS revenue
                FROM sales_line AS l
                LEFT JOIN item_details AS i
                       ON i.company_id = l.company_id AND i.item_no = l.item_no AND i.variant_code = l.variant_code
                WHERE l.company_id = '{co}' AND toDate(l.date) BETWEEN '{f}' AND '{t}'
                GROUP BY l.item_no ORDER BY revenue DESC LIMIT 15");
            foreach (var r in topItems)
                response.TopItems.Add(new SalesItemRow
                {
                    ItemNo = GetString(r, "item_no"),
                    Description = GetString(r, "description"),
                    Units = GetDouble(r, "units"),
                    Revenue = GetDouble(r, "revenue")
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sales overview aggregation failed for company {CompanyId}", companyId);
            throw;
        }

        return response;
    }

    private Task<List<Dictionary<string, object?>>> QueryAsync(MetricDataSource source, string sql)
        => _clickHouse.ExecuteQueryAsync(source, sql);

    // ClickHouse (via JSONEachRow) returns numbers as long/double and everything else as string.
    private static string GetString(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v != null ? v.ToString() ?? string.Empty : string.Empty;

    private static double GetDouble(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v == null) return 0;
        return v switch
        {
            double d => d,
            float fl => fl,
            decimal m => (double)m,
            long l => l,
            int i => i,
            _ => double.TryParse(v.ToString(), NumberStyles.Any, Inv, out var p) ? p : 0
        };
    }

    private static long GetLong(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v == null) return 0;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            decimal m => (long)m,
            _ => long.TryParse(v.ToString(), NumberStyles.Any, Inv, out var p) ? p : 0
        };
    }
}

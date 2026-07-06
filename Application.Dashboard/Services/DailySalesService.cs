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
/// Builds the Daily Sales drill-down live from the shared ClickHouse <c>sales_dataset</c> warehouse
/// (SalesDataset appsettings section). Each request aggregates one level of the
/// scheme → store → division → category hierarchy in a single scan: <c>sumIf</c> / <c>uniqExactIf</c>
/// compute the current, last-month and last-year windows together, so a row's vs-LM / vs-LY deltas
/// come back in one round-trip. Windows are calendar-aligned — the last-month / last-year windows are
/// the current window with its anchor date shifted back one month / one year.
/// </summary>
public class DailySalesService : IDailySalesService
{
    private readonly IClickHouseService _clickHouse;
    private readonly SalesDatasetSettings _settings;
    private readonly ILogger<DailySalesService> _logger;

    private static readonly Regex SafeToken = new("^[A-Za-z0-9_\\-]+$", RegexOptions.Compiled);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Distinct receipt key — a "transaction"/basket at any grouping level.
    private const string Basket = "(l.store_code, l.pos_number, l.transaction_no, l.receipt_no)";

    public DailySalesService(
        IClickHouseService clickHouse,
        IOptions<SalesDatasetSettings> settings,
        ILogger<DailySalesService> logger)
    {
        _clickHouse = clickHouse;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DailySalesResponse> GetAsync(
        string companyId, DateTime date, string scope, string view,
        string level, string? scheme, string? store, string? division,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId) || !SafeToken.IsMatch(companyId))
            throw new ArgumentException("Invalid company id.", nameof(companyId));

        var sc = (scope ?? string.Empty).ToUpperInvariant();
        scope = sc == "YTD" ? "YTD" : sc == "DAY" ? "Day" : "MTD";
        view = string.Equals(view, "ToDate", StringComparison.OrdinalIgnoreCase) ? "ToDate" : "Day";
        level = (level ?? "scheme").ToLowerInvariant() switch
        {
            "store" => "store",
            "division" => "division",
            "category" => "category",
            _ => "scheme"
        };

        var response = new DailySalesResponse { Date = date, Scope = scope, View = view };

        if (string.IsNullOrWhiteSpace(_settings.Host) || string.IsNullOrWhiteSpace(_settings.Database))
            return response; // not configured

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

        // Calendar-aligned windows: shift the anchor back a month / a year and recompute the same span.
        // The comparison window has the SAME shape as the displayed value (the scope window); the
        // Day/To-Date toggle only changes how far the anchor is shifted back:
        //  • Day     → weekday-aligned (Mon↔Mon): 4 weeks back for LM, 52 weeks back for LY.
        //  • To-Date → calendar-aligned (same date): one month back for LM, one year back for LY.
        // So a single-day scope (Yesterday) compares day-to-day in both views; MTD/YTD compare
        // period-to-date, with the whole window shifted a month / a year.
        var valueWin = ValueWindow(date, scope);
        var lmAnchor = view == "Day" ? date.AddDays(-28) : date.AddMonths(-1);
        var lyAnchor = view == "Day" ? date.AddDays(-364) : date.AddYears(-1);
        var cmpCur = valueWin;
        var cmpLm = ValueWindow(lmAnchor, scope);
        var cmpLy = ValueWindow(lyAnchor, scope);

        response.CurrentLabel = Label(valueWin);
        response.LastMonthLabel = Label(cmpLm);
        response.LastYearLabel = Label(cmpLy);

        var valCond = Cond(valueWin);
        var curCond = Cond(cmpCur);
        var lmCond = Cond(cmpLm);
        var lyCond = Cond(cmpLy);
        var co = Esc(companyId);

        var agg = $@"
            sumIf(l.net_amount_acy, {valCond})  AS val_sales,
            uniqExactIf({Basket}, {valCond})    AS val_tx,
            sumIf(l.net_amount_acy, {curCond})  AS cur_sales,
            uniqExactIf({Basket}, {curCond})    AS cur_tx,
            sumIf(l.net_amount_acy, {lmCond})   AS lm_sales,
            uniqExactIf({Basket}, {lmCond})     AS lm_tx,
            sumIf(l.net_amount_acy, {lyCond})   AS ly_sales,
            uniqExactIf({Basket}, {lyCond})     AS ly_tx";

        var window = $"l.company_id = '{co}' AND (({valCond}) OR ({curCond}) OR ({lmCond}) OR ({lyCond}))";

        string sql;
        bool childLevel;
        switch (level)
        {
            case "store":
                childLevel = true;
                sql = $@"
                    SELECT l.store_code AS grp_key,
                           any(coalesce(nullIf(s.store, ''), l.store_code)) AS grp_label,
                           {agg}
                    FROM sales_line AS l
                    LEFT JOIN store AS s ON s.company_id = l.company_id AND s.code = l.store_code
                    WHERE {window}
                      AND coalesce(nullIf(s.store_scheme, ''), 'Unknown') = '{Esc(scheme)}'
                    GROUP BY grp_key ORDER BY val_sales DESC";
                break;

            case "division":
                childLevel = true;
                sql = $@"
                    SELECT coalesce(nullIf(i.division_code, ''), 'UNKNOWN') AS grp_key,
                           any(coalesce(nullIf(ph.division, ''), nullIf(i.division_code, ''), 'Unknown')) AS grp_label,
                           {agg}
                    FROM sales_line AS l
                    {StoreJoin(scheme)}
                    LEFT JOIN item_details AS i
                           ON i.company_id = l.company_id AND i.item_no = l.item_no AND i.variant_code = l.variant_code
                    LEFT JOIN (SELECT company_id, division_code, any(division) AS division
                               FROM product_hierarchy GROUP BY company_id, division_code) AS ph
                           ON ph.company_id = i.company_id AND ph.division_code = i.division_code
                    WHERE {window}{ScopeFilter(scheme, store)}
                    GROUP BY grp_key ORDER BY val_sales DESC";
                break;

            case "category":
                childLevel = false;
                sql = $@"
                    SELECT coalesce(nullIf(i.category_code, ''), 'UNKNOWN') AS grp_key,
                           any(coalesce(nullIf(ph.category, ''), nullIf(i.category_code, ''), 'Unknown')) AS grp_label,
                           {agg}
                    FROM sales_line AS l
                    {StoreJoin(scheme)}
                    LEFT JOIN item_details AS i
                           ON i.company_id = l.company_id AND i.item_no = l.item_no AND i.variant_code = l.variant_code
                    LEFT JOIN (SELECT company_id, category_code, any(category) AS category
                               FROM product_hierarchy GROUP BY company_id, category_code) AS ph
                           ON ph.company_id = i.company_id AND ph.category_code = i.category_code
                    WHERE {window}{ScopeFilter(scheme, store)}
                      AND coalesce(nullIf(i.division_code, ''), 'UNKNOWN') = '{Esc(division)}'
                    GROUP BY grp_key ORDER BY val_sales DESC";
                break;

            default: // scheme
                childLevel = true;
                sql = $@"
                    SELECT coalesce(nullIf(s.store_scheme, ''), 'Unknown') AS grp_key,
                           any(coalesce(nullIf(s.store_scheme, ''), 'Unknown')) AS grp_label,
                           {agg}
                    FROM sales_line AS l
                    LEFT JOIN store AS s ON s.company_id = l.company_id AND s.code = l.store_code
                    WHERE {window}
                    GROUP BY grp_key ORDER BY val_sales DESC";
                break;
        }

        try
        {
            var rows = await _clickHouse.ExecuteQueryAsync(source, sql);
            foreach (var r in rows)
            {
                var row = new DailySalesRow
                {
                    Level = level,
                    Key = GetString(r, "grp_key"),
                    Label = GetString(r, "grp_label"),
                    HasChildren = childLevel,
                    Value = new DailySalesFigures { Sales = GetDouble(r, "val_sales"), Transactions = GetLong(r, "val_tx") },
                    CmpCurrent = new DailySalesFigures { Sales = GetDouble(r, "cur_sales"), Transactions = GetLong(r, "cur_tx") },
                    CmpLastMonth = new DailySalesFigures { Sales = GetDouble(r, "lm_sales"), Transactions = GetLong(r, "lm_tx") },
                    CmpLastYear = new DailySalesFigures { Sales = GetDouble(r, "ly_sales"), Transactions = GetLong(r, "ly_tx") }
                };
                response.Rows.Add(row);
            }

            // Grand totals for the cards — computed from the (complete) scheme level only.
            if (level == "scheme")
            {
                response.Totals = new DailySalesRow
                {
                    Level = "total",
                    Label = "All stores",
                    Value = Sum(response.Rows, x => x.Value),
                    CmpCurrent = Sum(response.Rows, x => x.CmpCurrent),
                    CmpLastMonth = Sum(response.Rows, x => x.CmpLastMonth),
                    CmpLastYear = Sum(response.Rows, x => x.CmpLastYear)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daily sales aggregation failed for company {CompanyId} at level {Level}", companyId, level);
            throw;
        }

        return response;
    }

    // Displayed value window — depends only on scope (never on the Day/To-Date toggle).
    private static (DateTime From, DateTime To) ValueWindow(DateTime asOf, string scope)
        => scope switch
        {
            "YTD" => (new DateTime(asOf.Year, 1, 1), asOf.Date),
            "MTD" => (new DateTime(asOf.Year, asOf.Month, 1), asOf.Date),
            _ => (asOf.Date, asOf.Date) // Yesterday
        };

    private static string Cond((DateTime From, DateTime To) w)
        => $"toDate(l.date) BETWEEN '{w.From:yyyy-MM-dd}' AND '{w.To:yyyy-MM-dd}'";

    private static string Label((DateTime From, DateTime To) w)
        => w.From == w.To
            ? w.To.ToString("MMM d, yyyy", Inv)
            : $"{w.From.ToString("MMM d", Inv)} – {w.To.ToString("MMM d, yyyy", Inv)}";

    private static DailySalesFigures Sum(IEnumerable<DailySalesRow> rows, Func<DailySalesRow, DailySalesFigures> pick)
    {
        double s = 0; long t = 0;
        foreach (var r in rows) { var f = pick(r); s += f.Sales; t += f.Transactions; }
        return new DailySalesFigures { Sales = s, Transactions = t };
    }

    // Division/category can be scoped by scheme (spans its stores) or a single store. Filtering by
    // scheme needs the store table joined; filtering by store uses sales_line.store_code directly.
    private static string StoreJoin(string? scheme)
        => string.IsNullOrWhiteSpace(scheme)
            ? string.Empty
            : "LEFT JOIN store AS s ON s.company_id = l.company_id AND s.code = l.store_code";

    private static string ScopeFilter(string? scheme, string? store)
    {
        var sb = string.Empty;
        if (!string.IsNullOrWhiteSpace(scheme))
            sb += $" AND coalesce(nullIf(s.store_scheme, ''), 'Unknown') = '{Esc(scheme)}'";
        if (!string.IsNullOrWhiteSpace(store))
            sb += $" AND l.store_code = '{Esc(store)}'";
        return sb;
    }

    private static string Esc(string? v) => (v ?? string.Empty).Replace("'", "''");

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

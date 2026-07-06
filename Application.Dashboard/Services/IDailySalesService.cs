using Application.Shared.Models.Dashboards.Sales;

namespace Application.Dashboard.Services;

public interface IDailySalesService
{
    /// <summary>
    /// Builds one level of the Daily Sales drill-down for a company. <paramref name="level"/> is
    /// <c>scheme</c> (default, also returns grand totals for the cards), <c>store</c>, <c>division</c>
    /// or <c>category</c>; the parent keys scope deeper levels. Each row carries the current window plus
    /// the calendar-aligned last-month / last-year windows, derived from <paramref name="scope"/>
    /// (MTD/YTD) and <paramref name="view"/> (Day/ToDate) anchored on <paramref name="date"/>.
    /// </summary>
    Task<DailySalesResponse> GetAsync(
        string companyId, DateTime date, string scope, string view,
        string level, string? scheme, string? store, string? division,
        CancellationToken ct = default);
}

using Application.Shared.Models.Dashboards.Sales;

namespace Application.Dashboard.Services;

public interface ISalesOverviewService
{
    /// <summary>
    /// Builds the Sales Performance Overview for a company over the trailing <paramref name="days"/>
    /// window, aggregating live from the company's ClickHouse data source. Returns a response with
    /// <see cref="SalesOverviewResponse.Configured"/> = false when no ClickHouse source is configured.
    /// </summary>
    Task<SalesOverviewResponse> GetAsync(string companyId, int days, CancellationToken ct = default);
}

using Application.Shared.Authorization;
using Application.Shared.Models.Dashboards.Oos;
using Application.Shared.Models.Dashboards.Sales;
using Application.Dashboard.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = PolicyNames.DashboardsRead)]
public class DashboardsController : ControllerBase
{
    private readonly IOosDashboardService _oos;
    private readonly ISalesOverviewService _sales;
    private readonly IDailySalesService _dailySales;

    public DashboardsController(IOosDashboardService oos, ISalesOverviewService sales, IDailySalesService dailySales)
    {
        _oos = oos;
        _sales = sales;
        _dailySales = dailySales;
    }

    /// <summary>Out-of-Stock dashboard dataset for the company at a given as-of date.</summary>
    [HttpGet("oos")]
    [Authorize(Policy = PolicyNames.InventoryRead)]
    public async Task<ActionResult<OosDashboardResponse>> GetOos(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        [FromQuery] DateTime? asOf = null,
        [FromQuery] int limit = 50000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        if (!User.HasAllCompanyRoles(companyId, RoleSuffixes.DashboardsRead, RoleSuffixes.InventoryRead))
            return Forbid();

        var data = await _oos.GetAsync(companyId, asOf ?? DateTime.Today, limit, cancellationToken);
        return Ok(data);
    }

    /// <summary>Sales Performance Overview dataset for the company over the trailing day window.</summary>
    [HttpGet("sales-overview")]
    [Authorize(Policy = PolicyNames.SalesRead)]
    public async Task<ActionResult<SalesOverviewResponse>> GetSalesOverview(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        if (!User.HasAllCompanyRoles(companyId, RoleSuffixes.DashboardsRead, RoleSuffixes.Sales))
            return Forbid();

        var data = await _sales.GetAsync(companyId, days, cancellationToken);
        return Ok(data);
    }

    /// <summary>
    /// Daily Sales drill-down for the company. One call returns a single hierarchy level
    /// (scheme → store → division → category); the scheme level also carries grand totals.
    /// </summary>
    [HttpGet("daily-sales")]
    [Authorize(Policy = PolicyNames.SalesRead)]
    public async Task<ActionResult<DailySalesResponse>> GetDailySales(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        [FromQuery] DateTime? date = null,
        [FromQuery] string scope = "MTD",
        [FromQuery] string view = "Day",
        [FromQuery] string level = "scheme",
        [FromQuery] string? scheme = null,
        [FromQuery] string? store = null,
        [FromQuery] string? division = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        if (!User.HasAllCompanyRoles(companyId, RoleSuffixes.DashboardsRead, RoleSuffixes.Sales))
            return Forbid();

        // Default anchor = yesterday.
        var anchor = date ?? DateTime.Today.AddDays(-1);

        var data = await _dailySales.GetAsync(
            companyId, anchor, scope, view, level, scheme, store, division, cancellationToken);
        return Ok(data);
    }
}

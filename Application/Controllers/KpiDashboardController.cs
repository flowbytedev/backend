using Application.Shared.Authorization;
using Application.Shared.Models.Metrics;
using Application.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

[Route("api/kpi-dashboard")]
[ApiController]
[Authorize(Policy = PolicyNames.MetricsRead)]
public class KpiDashboardController : ControllerBase
{
    private readonly IKpiDashboardService _service;

    public KpiDashboardController(IKpiDashboardService service)
    {
        _service = service;
    }

    // GET: api/kpi-dashboard
    [HttpGet]
    public async Task<ActionResult<List<KpiCardDto>>> GetCards(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        if (!User.HasCompanyRole(companyId, RoleSuffixes.MetricsRead, RoleSuffixes.MetricsWrite))
            return Forbid();

        var cards = await _service.GetKpiCardsAsync(companyId, cancellationToken);
        return Ok(cards);
    }

    // GET: api/kpi-dashboard/trend?department=&kpi=
    [HttpGet("trend")]
    public async Task<ActionResult<List<KpiTrendPointDto>>> GetTrend(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        [FromQuery] string? department,
        [FromQuery] string? kpi,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        if (string.IsNullOrWhiteSpace(kpi))
            return BadRequest("KPI is required");

        if (!User.HasCompanyRole(companyId, RoleSuffixes.MetricsRead, RoleSuffixes.MetricsWrite))
            return Forbid();

        var points = await _service.GetKpiTrendAsync(companyId, department ?? string.Empty, kpi, cancellationToken);
        return Ok(points);
    }
}

using Application.Shared.Authorization;
using Application.Shared.Models.Logging;
using Application.Shared.Services.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Read access to the Datasets/Tables audit log (ClickHouse <c>data_app_log</c>). Gated by the
/// <see cref="PolicyNames.DataLogRead"/> policy — <c>{company}_DATA_ADMIN</c> or <c>{company}_ADMIN</c>.
/// Always scoped to the caller's company.
/// </summary>
[Route("api/data-log")]
[ApiController]
[Authorize(Policy = PolicyNames.DataLogRead)]
public class ActivityLogController : ControllerBase
{
    private readonly IDataAppLogService _log;

    public ActivityLogController(IDataAppLogService log) => _log = log;

    [HttpGet]
    public async Task<ActionResult<DataAppLogQueryResult>> Get(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? source = null,
        [FromQuery] string? area = null,
        [FromQuery] string? action = null,
        [FromQuery] string? userId = null,
        [FromQuery] string? datasetId = null,
        [FromQuery] string? tableName = null,
        [FromQuery] string? level = null,
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        // Defense-in-depth beyond the module policy: ADMIN passes HasCompanyRole implicitly.
        if (!User.HasCompanyRole(companyId, RoleSuffixes.DataAdmin))
            return Forbid();

        var result = await _log.QueryAsync(new DataAppLogQuery
        {
            CompanyId = companyId,
            From = from,
            To = to,
            Source = source,
            Area = area,
            Action = action,
            UserId = userId,
            DatasetId = datasetId,
            TableName = tableName,
            Level = level,
            Category = category,
            Search = search,
            Page = page,
            PageSize = pageSize
        }, cancellationToken);

        return Ok(result);
    }
}

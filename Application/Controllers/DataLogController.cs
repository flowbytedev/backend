using System.Security.Claims;
using Application.Shared.Authorization;
using Application.Shared.Models.Logging;
using Application.Shared.Services.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Receives pure client-side UI events (page open, client-only sort/search) that never hit a
/// dataset/table endpoint, and records them in the same <c>data_app_log</c> as server-side actions.
/// </summary>
[Route("api/data-log")]
[ApiController]
[Authorize(Policy = PolicyNames.DatasetsAccess)]
public class DataLogController : ControllerBase
{
    private readonly IDataAppLogService _log;

    public DataLogController(IDataAppLogService log) => _log = log;

    [HttpPost("ui-event")]
    public IActionResult LogUiEvent([FromBody] UiActivityLogRequest request)
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");
        if (!User.HasCompanyRole(companyId, "VIEW_DATA"))
            return Forbid();

        if (request is null || string.IsNullOrWhiteSpace(request.Action))
            return BadRequest("Action is required");

        // Accept the event even when logging is disabled/misconfigured — Enqueue is a safe no-op then.
        _log.Enqueue(new DataAppLogEntry
        {
            EventTime = DateTime.UtcNow,
            CompanyId = companyId,
            UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? Request.Headers["UserId"].FirstOrDefault() ?? string.Empty,
            UserName = User.Identity?.Name
                       ?? User.FindFirst("preferred_username")?.Value
                       ?? User.FindFirst("name")?.Value ?? string.Empty,
            Source = "client",
            Area = DeriveArea(request),
            Action = request.Action,
            DatasetId = request.DatasetId ?? string.Empty,
            TableName = request.TableName ?? string.Empty,
            HttpMethod = "UI",
            Route = request.Route ?? string.Empty,
            QueryText = string.Empty,
            Details = request.Details is { Length: > 16000 } d ? d[..16000] : request.Details ?? string.Empty,
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 512 } ua ? ua[..512] : Request.Headers.UserAgent.ToString(),
            StatusCode = 200,
            Success = true
        });

        return Accepted();
    }

    private static string DeriveArea(UiActivityLogRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Area)) return request.Area!;
        var dot = request.Action.IndexOf('.');
        return dot > 0 ? request.Action[..dot] : "table";
    }
}

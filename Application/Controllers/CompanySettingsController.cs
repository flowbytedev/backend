using Application.Shared.Authorization;
using Application.Shared.Models;
using Application.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Per-company application settings. ADMIN-only (<c>{companyId}_ADMIN</c>). Currently exposes the
/// debug-logging toggle that gates <see cref="Application.Shared.Services.Logging.IDebugLogService"/>
/// and controls whether the client debug-log side panel appears.
/// </summary>
[Route("api/company-settings")]
[ApiController]
[Authorize]
public class CompanySettingsController : ControllerBase
{
    private readonly ICompanySettingsService _settings;

    public CompanySettingsController(ICompanySettingsService settings) => _settings = settings;

    [HttpGet]
    public async Task<ActionResult<CompanySettingsDto>> Get(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");
        if (!User.HasCompanyRole(companyId, RoleSuffixes.Admin))
            return Forbid();

        var settings = await _settings.GetAsync(companyId, cancellationToken);
        return Ok(new CompanySettingsDto { DebugLoggingEnabled = settings.DebugLoggingEnabled });
    }

    [HttpPut]
    public async Task<ActionResult<CompanySettingsDto>> Put(
        [FromHeader(Name = "X-Company-Id")] string? companyId,
        [FromBody] CompanySettingsDto body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");
        if (!User.HasCompanyRole(companyId, RoleSuffixes.Admin))
            return Forbid();
        if (body == null)
            return BadRequest("Settings body is required");

        var userId = Request.Headers["UserId"].FirstOrDefault();
        await _settings.SetDebugLoggingAsync(companyId, body.DebugLoggingEnabled, userId, cancellationToken);
        return Ok(new CompanySettingsDto { DebugLoggingEnabled = body.DebugLoggingEnabled });
    }
}

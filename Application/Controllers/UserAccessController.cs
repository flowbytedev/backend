using Application.Shared.Authorization;
using Application.Shared.Models;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Data-admin User Access management: grant a user access to a dataset's tables/columns and set their
/// row-level-security filters, in one atomic call. Gated by the DATA_ADMIN role (ADMIN also passes) so a
/// data admin can manage access without needing the EDIT_DATA sharing role.
/// </summary>
[Route("api/user-access")]
[ApiController]
[Authorize(Policy = PolicyNames.DataAdminAccess)]
public class UserAccessController : ControllerBase
{
    private readonly IDatasetSharingService _sharing;

    public UserAccessController(IDatasetSharingService sharing) => _sharing = sharing;

    private string CompanyId => Request.Headers["X-Company-ID"].FirstOrDefault() ?? string.Empty;

    // GET: api/user-access/{datasetId}/{userId} — the user's current access to the dataset (for prefill).
    [HttpGet("{datasetId}/{userId}")]
    public async Task<ActionResult<UserDatasetAccessDto>> Get(string datasetId, string userId)
    {
        if (!User.HasCompanyRole(CompanyId, "DATA_ADMIN"))
            return Forbid();
        return Ok(await _sharing.GetUserDatasetAccessAsync(datasetId, userId));
    }

    // PUT: api/user-access/{datasetId}/{userId} — apply the user's full access (tables + columns + RLS).
    [HttpPut("{datasetId}/{userId}")]
    public async Task<ActionResult> Set(string datasetId, string userId, [FromBody] SetUserAccessRequest request)
    {
        if (!User.HasCompanyRole(CompanyId, "DATA_ADMIN"))
            return Forbid();
        if (string.IsNullOrWhiteSpace(CompanyId))
            return BadRequest("Company ID is required");
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required");

        var ok = await _sharing.SetUserDatasetAccessAsync(CompanyId, datasetId, userId, request);
        return ok ? Ok(new { message = "Access updated" }) : BadRequest("Failed to update access. Dataset or user not found.");
    }
}

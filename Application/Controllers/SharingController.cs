using Application.Shared.Authorization;
using Application.Shared.Models;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

[Route("api/datasets/{datasetId}/[controller]")]
[ApiController]
public class SharingController : ControllerBase
{
    private readonly IDatasetSharingService _datasetSharingService;

    public SharingController(IDatasetSharingService datasetSharingService)
    {
        _datasetSharingService = datasetSharingService;
    }

    // GET: api/datasets/{datasetId}/sharing
    [HttpGet]
    public async Task<ActionResult<List<DatasetUserDto>>> GetDatasetUsers(string datasetId, [FromHeader(Name = "X-Company-Id")] string companyId = "")
    {
        try
        {
            if (!User.HasCompanyRole(companyId, "VIEW_DATA"))
                return Forbid();

            var users = await _datasetSharingService.GetDatasetUsersAsync(datasetId);
            return Ok(users);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error retrieving dataset users: {ex.Message}");
        }
    }

    // POST: api/datasets/{datasetId}/sharing
    [HttpPost]
    public async Task<ActionResult> ShareDataset(string datasetId, [FromBody] ShareDatasetRequest request, [FromHeader(Name = "X-Company-Id")] string companyId = "")
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Email is required");

        if (datasetId != request.DatasetId)
            return BadRequest("Dataset ID mismatch");

        try
        {
            var success = await _datasetSharingService.ShareDatasetAsync(request, userId);
            
            if (!success)
                return BadRequest("Failed to share dataset. User may not exist or dataset not found.");

            return Ok(new { message = "Dataset shared successfully" });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error sharing dataset: {ex.Message}");
        }
    }

    // POST: api/datasets/{datasetId}/sharing/grant-table — additively share a single table with a user.
    [HttpPost("grant-table")]
    public async Task<ActionResult> GrantTable(string datasetId, [FromBody] GrantTableShareRequest request, [FromHeader(Name = "X-Company-Id")] string companyId = "")
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (request == null || string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Email is required");
        if (string.IsNullOrWhiteSpace(request.TableName))
            return BadRequest("Table name is required");
        if (datasetId != request.DatasetId)
            return BadRequest("Dataset ID mismatch");

        try
        {
            var success = await _datasetSharingService.GrantTableAccessAsync(request, userId);
            if (!success)
                return BadRequest("Failed to share table. User may not exist or dataset not found.");

            return Ok(new { message = "Table shared successfully" });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error sharing table: {ex.Message}");
        }
    }

    // PUT: api/datasets/{datasetId}/sharing/{userId}
    [HttpPut("{userId}")]
    public async Task<ActionResult> UpdateUserAccess(string datasetId, string userId, [FromBody] DatasetUserType userType, [FromHeader(Name = "X-Company-Id")] string companyId = "")
    {
        try
        {
            if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
                return Forbid();

            var success = await _datasetSharingService.UpdateDatasetUserTypeAsync(datasetId, userId, userType);
            
            if (!success)
                return NotFound("Dataset user not found");

            return Ok(new { message = "User access updated successfully" });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error updating user access: {ex.Message}");
        }
    }

    // DELETE: api/datasets/{datasetId}/sharing/{userId}/tables/{tableName} — stop sharing a single table
    // with a table-scoped user (removes the whole share if it was their only table).
    [HttpDelete("{userId}/tables/{tableName}")]
    public async Task<ActionResult> RevokeTableAccess(string datasetId, string userId, string tableName, [FromHeader(Name = "X-Company-Id")] string companyId = "")
    {
        try
        {
            if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
                return Forbid();

            var success = await _datasetSharingService.RevokeTableAccessAsync(datasetId, userId, tableName);

            if (!success)
                return NotFound("Table share not found, or the user has full dataset access (manage from dataset sharing).");

            return Ok(new { message = "Table access removed successfully" });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error removing table access: {ex.Message}");
        }
    }

    // DELETE: api/datasets/{datasetId}/sharing/{userId}
    [HttpDelete("{userId}")]
    public async Task<ActionResult> RemoveUserAccess(string datasetId, string userId, [FromHeader(Name = "X-Company-Id")] string companyId = "")
    {
        try
        {
            if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
                return Forbid();

            var success = await _datasetSharingService.RemoveDatasetUserAsync(datasetId, userId);
            
            if (!success)
                return NotFound("Dataset user not found");

            return Ok(new { message = "User access removed successfully" });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error removing user access: {ex.Message}");
        }
    }
}

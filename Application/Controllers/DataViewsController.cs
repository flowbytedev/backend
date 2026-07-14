using Application.Shared.Authorization;
using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Mvc;
using Application.Attributes;

namespace Application.Controllers;

[Route("api/[controller]")]
[ApiController]
[RequireCompanyHeader]
public class DataViewsController : ControllerBase
{
    private readonly ICommentService _commentService;
    private readonly IUserPreferencesService _userPreferencesService;
    private readonly IUserSearchService _userSearchService;

    public DataViewsController(
        ICommentService commentService,
        IUserPreferencesService userPreferencesService,
        IUserSearchService userSearchService)
    {
        _commentService = commentService;
        _userPreferencesService = userPreferencesService;
        _userSearchService = userSearchService;
    }

    // GET: api/DataViews/comments/{datasetId}/{tableName}
    [HttpGet("comments/{datasetId}/{tableName}")]
    public async Task<ActionResult<List<DataTableComment>>> GetComments(string datasetId, string tableName)
    {
        try
        {
            var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
            if (!User.HasCompanyRole(companyId, "VIEW_DATA"))
                return Forbid();

            var comments = await _commentService.GetCommentsAsync(datasetId, tableName);
            return Ok(comments);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error retrieving comments: {ex.Message}");
        }
    }

    // POST: api/DataViews/comments
    [HttpPost("comments")]
    public async Task<ActionResult<DataTableComment>> AddComment([FromBody] DataTableComment comment)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            comment.UserId = userId;
            var addedComment = await _commentService.AddCommentAsync(comment, companyId);
            return CreatedAtAction(nameof(GetComments), 
                new { datasetId = comment.DatasetId, tableName = comment.TableName }, addedComment);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error adding comment: {ex.Message}");
        }
    }

    // DELETE: api/DataViews/comments/{commentId}
    [HttpDelete("comments/{commentId}")]
    public async Task<IActionResult> DeleteComment(string commentId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            var deleted = await _commentService.DeleteCommentAsync(commentId, userId);
            if (!deleted)
                return NotFound("Comment not found or access denied");

            return NoContent();
        }
        catch (Exception ex)
        {
            return BadRequest($"Error deleting comment: {ex.Message}");
        }
    }

    // PUT: api/DataViews/comments/{commentId}
    [HttpPut("comments/{commentId}")]
    public async Task<ActionResult<DataTableComment>> UpdateComment(string commentId, [FromBody] string content)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            var updatedComment = await _commentService.UpdateCommentAsync(commentId, content, userId);
            if (updatedComment == null)
                return NotFound("Comment not found or access denied");

            return Ok(updatedComment);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error updating comment: {ex.Message}");
        }
    }

    // GET: api/DataViews/preferences/{datasetId}/{tableName}
    [HttpGet("preferences/{datasetId}/{tableName}")]
    public async Task<ActionResult<UserColumnPreferences>> GetUserPreferences(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA"))
            return Forbid();

        try
        {
            var preferences = await _userPreferencesService.GetUserColumnPreferencesAsync(userId, datasetId, tableName);
            return Ok(preferences);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error retrieving preferences: {ex.Message}");
        }
    }

    // POST: api/DataViews/preferences
    [HttpPost("preferences")]
    public async Task<ActionResult<UserColumnPreferences>> SaveUserPreferences([FromBody] UserColumnPreferences preferences)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            preferences.UserId = userId;
            var savedPreferences = await _userPreferencesService.SaveUserColumnPreferencesAsync(preferences);
            return Ok(savedPreferences);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error saving preferences: {ex.Message}");
        }
    }

    // GET: api/DataViews/users/search
    [HttpGet("users/search")]
    public async Task<ActionResult<List<UserMention>>> SearchUsers([FromQuery] string? searchTerm = null, [FromQuery] int maxResults = 5)
    {
        var companyId = Request.Headers["X-Company-ID"].ToString();
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        // Mention autocomplete is shared by data-table comments (VIEW_DATA) and notebook cell comments
        // (QUERY), plus data-admin tooling (DATA_ADMIN) — any of these may @-mention a teammate.
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        try
        {
            var users = await _userSearchService.SearchUsersAsync(companyId, searchTerm ?? string.Empty, maxResults);
            return Ok(users);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error searching users: {ex.Message}");
        }
    }
}

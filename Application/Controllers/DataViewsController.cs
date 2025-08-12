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

        try
        {
            comment.UserId = userId;
            var addedComment = await _commentService.AddCommentAsync(comment);
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
    public async Task<ActionResult<List<UserMention>>> SearchUsers([FromQuery] string searchTerm, [FromQuery] int maxResults = 5)
    {
        var companyId = Request.Headers["X-Company-ID"].ToString();
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required in headers");

        if (string.IsNullOrWhiteSpace(searchTerm))
            return BadRequest("Search term is required");

        try
        {
            var users = await _userSearchService.SearchUsersAsync(companyId, searchTerm, maxResults);
            return Ok(users);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error searching users: {ex.Message}");
        }
    }
}

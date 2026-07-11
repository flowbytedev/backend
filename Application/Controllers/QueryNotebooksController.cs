using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Shared.Authorization;
using Application.Shared.Models.Notebooks;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// MotherDuck-style SQL notebooks: ordered cells (SQL/Markdown), each SQL cell picking its own dataset.
/// Reading/listing requires VIEW_DATA; every mutation (including running a cell, which always
/// materializes a table/view as a side effect) requires EDIT_DATA — the same bar as the Query Workbench's
/// "save result as table/view" endpoint.
/// </summary>
[Route("api/query-notebooks")]
[ApiController]
[Authorize(Policy = PolicyNames.DatasetsAccess)]
public class QueryNotebooksController : ControllerBase
{
    private readonly IQueryNotebookService _notebooks;
    private readonly INotebookAgentService _agent;
    private readonly INotebookSharingService _sharing;
    private readonly INotebookCommentService _comments;
    private readonly INotebookRunCancellationRegistry _cancellation;

    public QueryNotebooksController(
        IQueryNotebookService notebooks, INotebookAgentService agent, INotebookSharingService sharing,
        INotebookCommentService comments, INotebookRunCancellationRegistry cancellation)
    {
        _notebooks = notebooks;
        _agent = agent;
        _sharing = sharing;
        _comments = comments;
        _cancellation = cancellation;
    }

    [HttpGet]
    public async Task<ActionResult<List<QueryNotebookDto>>> List()
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        return Ok(await _notebooks.GetForCompanyAsync(companyId, userId, isAdmin));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<QueryNotebookDto>> Get(string id)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();

        var notebook = await _notebooks.GetAsync(companyId, id, userId, isAdmin);
        return notebook == null ? NotFound() : Ok(notebook);
    }

    [HttpPost]
    public async Task<ActionResult<QueryNotebookDto>> Create([FromBody] SaveNotebookRequest request)
    {
        var (companyId, userId, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        return Ok(await _notebooks.CreateAsync(companyId, userId, request ?? new()));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<QueryNotebookDto>> Rename(string id, [FromBody] SaveNotebookRequest request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        var updated = await _notebooks.RenameAsync(companyId, id, userId, isAdmin, request ?? new());
        return updated == null ? NotFound("Notebook not found, or you don't have permission to edit it.") : Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return await _notebooks.DeleteAsync(companyId, id, userId, isAdmin, HttpContext.RequestAborted)
            ? NoContent()
            : NotFound("Notebook not found, or you don't have permission to delete it.");
    }

    [HttpPost("{id}/cells")]
    public async Task<ActionResult<NotebookCellDto>> AddCell(string id, [FromBody] SaveNotebookCellRequest request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");

        var (cell, cellError) = await _notebooks.AddCellAsync(companyId, userId, isAdmin, id, request);
        return cell == null ? BadRequest(cellError ?? "Couldn't add the cell.") : Ok(cell);
    }

    [HttpPut("{id}/cells/{cellId}")]
    public async Task<ActionResult<NotebookCellDto>> UpdateCell(string id, string cellId, [FromBody] SaveNotebookCellRequest request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");

        var (cell, cellError) = await _notebooks.UpdateCellAsync(companyId, userId, isAdmin, id, cellId, request, HttpContext.RequestAborted);
        return cell == null ? BadRequest(cellError ?? "Couldn't update the cell.") : Ok(cell);
    }

    [HttpDelete("{id}/cells/{cellId}")]
    public async Task<IActionResult> RemoveCell(string id, string cellId)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return await _notebooks.RemoveCellAsync(companyId, userId, isAdmin, id, cellId, HttpContext.RequestAborted) ? NoContent() : NotFound();
    }

    [HttpPut("{id}/cells/reorder")]
    public async Task<IActionResult> ReorderCells(string id, [FromBody] List<string> orderedCellIds)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (orderedCellIds == null || orderedCellIds.Count == 0) return BadRequest("No cell order provided.");

        return await _notebooks.ReorderCellsAsync(companyId, userId, isAdmin, id, orderedCellIds) ? NoContent() : NotFound();
    }

    [HttpPost("{id}/cells/{cellId}/run")]
    public async Task<ActionResult<NotebookCellRunResult>> RunCell(string id, string cellId, [FromBody] RunNotebookRequest? request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return Ok(await _notebooks.RunCellAsync(companyId, userId, isAdmin, id, cellId, request?.Parameters, "manual", HttpContext.RequestAborted));
    }

    [HttpPost("{id}/cells/{cellId}/cancel")]
    public IActionResult CancelCell(string id, string cellId)
    {
        var (_, _, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        return _cancellation.Cancel($"cell:{cellId}") ? Ok() : NotFound("No run is currently in progress for this cell.");
    }

    [HttpPost("{id}/run-all")]
    public async Task<ActionResult<RunAllResult>> RunAll(string id, [FromBody] RunNotebookRequest? request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return Ok(await _notebooks.RunAllAsync(companyId, userId, isAdmin, id, request?.Parameters, "run_all", HttpContext.RequestAborted));
    }

    [HttpPost("{id}/run-all/cancel")]
    public IActionResult CancelRunAll(string id)
    {
        var (_, _, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        return _cancellation.Cancel($"notebook:{id}:all") ? Ok() : NotFound("No Run All is currently in progress for this notebook.");
    }

    // ---- schedule ----

    [HttpPut("{id}/schedule")]
    public async Task<ActionResult<QueryNotebookDto>> UpdateSchedule(string id, [FromBody] ScheduleNotebookRequest request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");

        var updated = await _notebooks.UpdateScheduleAsync(companyId, id, userId, isAdmin, request);
        return updated == null
            ? NotFound("Notebook not found, you don't have permission to manage its schedule, or the schedule needs a cron expression to be enabled.")
            : Ok(updated);
    }

    // ---- run history ----

    [HttpGet("{id}/cells/{cellId}/runs")]
    public async Task<ActionResult<List<NotebookCellRunDto>>> GetCellRunHistory(string id, string cellId, [FromQuery] int take = 20)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();

        return Ok(await _notebooks.GetCellRunHistoryAsync(companyId, id, cellId, userId, isAdmin, take));
    }

    // ---- storage ----

    [HttpGet("{id}/storage")]
    public async Task<ActionResult<NotebookStorageSummaryDto>> GetStorage(string id)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();

        var summary = await _notebooks.GetStorageSummaryAsync(companyId, id, userId, isAdmin, HttpContext.RequestAborted);
        return summary == null ? NotFound() : Ok(summary);
    }

    // ---- duplicate / export / import ----

    [HttpPost("{id}/duplicate")]
    public async Task<ActionResult<QueryNotebookDto>> Duplicate(string id, [FromBody] DuplicateNotebookRequest? request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        var duplicate = await _notebooks.DuplicateAsync(companyId, id, userId, isAdmin, request?.Name, HttpContext.RequestAborted);
        return duplicate == null ? NotFound("Notebook not found, or you don't have permission to view it.") : Ok(duplicate);
    }

    [HttpGet("{id}/export")]
    public async Task<ActionResult<NotebookExportDto>> Export(string id)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();

        var export = await _notebooks.ExportAsync(companyId, id, userId, isAdmin);
        return export == null ? NotFound("Notebook not found, or you don't have permission to view it.") : Ok(export);
    }

    [HttpPost("import")]
    public async Task<ActionResult<QueryNotebookDto>> Import([FromBody] NotebookExportDto request)
    {
        var (companyId, userId, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");

        return Ok(await _notebooks.ImportAsync(companyId, userId, request));
    }

    // ---- sharing ----

    [HttpGet("{id}/sharing")]
    public async Task<ActionResult<List<NotebookUserDto>>> GetSharing(string id)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        var notebook = await _notebooks.GetAsync(companyId, id, userId, isAdmin);
        if (notebook == null) return NotFound();

        return Ok(await _sharing.GetNotebookUsersAsync(id));
    }

    [HttpPost("{id}/sharing")]
    public async Task<IActionResult> ShareNotebook(string id, [FromBody] ShareNotebookRequest request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        var notebook = await _notebooks.GetAsync(companyId, id, userId, isAdmin);
        if (notebook == null) return NotFound();
        if (!notebook.CanEdit) return Forbid(); // only the owner/company-admin manages sharing
        if (request == null || string.IsNullOrWhiteSpace(request.Email)) return BadRequest("Email is required");
        if (id != request.NotebookId) return BadRequest("Notebook ID mismatch");

        var success = await _sharing.ShareNotebookAsync(request, userId);
        return success ? Ok(new { message = "Notebook shared successfully" }) : BadRequest("Failed to share notebook. The user may not exist.");
    }

    [HttpPut("{id}/sharing/{userId}")]
    public async Task<IActionResult> UpdateSharing(string id, string userId, [FromBody] NotebookUserType userType)
    {
        var (companyId, requestUserId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        var notebook = await _notebooks.GetAsync(companyId, id, requestUserId, isAdmin);
        if (notebook == null) return NotFound();
        if (!notebook.CanEdit) return Forbid();

        return await _sharing.UpdateNotebookUserTypeAsync(id, userId, userType) ? Ok() : NotFound("Notebook user not found");
    }

    [HttpDelete("{id}/sharing/{userId}")]
    public async Task<IActionResult> RemoveSharing(string id, string userId)
    {
        var (companyId, requestUserId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        var notebook = await _notebooks.GetAsync(companyId, id, requestUserId, isAdmin);
        if (notebook == null) return NotFound();
        if (!notebook.CanEdit) return Forbid();

        return await _sharing.RemoveNotebookUserAsync(id, userId) ? Ok() : NotFound("Notebook user not found");
    }

    // ---- cell comments ----

    [HttpGet("{id}/cells/{cellId}/comments")]
    public async Task<ActionResult<List<NotebookCellComment>>> GetComments(string id, string cellId)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        var notebook = await _notebooks.GetAsync(companyId, id, userId, isAdmin);
        if (notebook == null) return NotFound();

        return Ok(await _comments.GetCommentsAsync(id, cellId));
    }

    [HttpPost("{id}/cells/{cellId}/comments")]
    public async Task<ActionResult<NotebookCellComment>> AddComment(string id, string cellId, [FromBody] NotebookCellComment comment)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        var notebook = await _notebooks.GetAsync(companyId, id, userId, isAdmin);
        if (notebook == null) return NotFound();
        if (comment == null || string.IsNullOrWhiteSpace(comment.Content)) return BadRequest("Content is required");

        comment.NotebookId = id;
        comment.CellId = cellId;
        comment.UserId = userId;
        return Ok(await _comments.AddCommentAsync(comment, companyId, notebook.Name));
    }

    [HttpPut("comments/{commentId}")]
    public async Task<ActionResult<NotebookCellComment>> UpdateComment(string commentId, [FromBody] string content)
    {
        var (_, userId, _, error) = ReadContext();
        if (error != null) return BadRequest(error);

        var updated = await _comments.UpdateCommentAsync(commentId, content, userId);
        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("comments/{commentId}")]
    public async Task<IActionResult> DeleteComment(string commentId)
    {
        var (_, userId, _, error) = ReadContext();
        if (error != null) return BadRequest(error);

        return await _comments.DeleteCommentAsync(commentId, userId) ? NoContent() : NotFound();
    }

    // POST: ai-assist — plans a suggested SQL body for one cell. Never executes/materializes anything.
    [HttpPost("{id}/cells/{cellId}/ai-assist")]
    public async Task<ActionResult<NotebookAiAssistResult>> AiAssist(string id, string cellId, [FromBody] NotebookAiAssistRequest request)
    {
        var (companyId, userId, isAdmin, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null || string.IsNullOrWhiteSpace(request.Message)) return BadRequest("Message is required");

        var notebook = await _notebooks.GetAsync(companyId, id, userId, isAdmin);
        if (notebook == null) return NotFound();
        var cell = notebook.Cells.FirstOrDefault(c => c.Id == cellId);
        if (cell == null) return NotFound("Cell not found.");
        if (string.IsNullOrWhiteSpace(cell.DatasetId)) return BadRequest("Pick a dataset for this cell first.");

        var priorCells = notebook.Cells
            .Where(c => c.SortOrder < cell.SortOrder && c.CellType == "sql")
            .Select(c => new NotebookPriorCellContext { Name = c.Name, CellType = c.CellType, Sql = c.Sql })
            .ToList();

        var referencedCells = notebook.Cells
            .Where(c => cell.ReferencedCellIds.Contains(c.Id) && !string.IsNullOrEmpty(c.LastMaterializedObject) && !string.IsNullOrEmpty(c.DatasetId))
            .Select(c => new NotebookReferencedCellContext { Name = c.LastMaterializedObject!, DatasetId = c.DatasetId! })
            .ToList();

        var agentRequest = new NotebookAgentRequest
        {
            CompanyId = companyId,
            UserId = userId,
            DatasetId = cell.DatasetId!,
            Message = request.Message.Trim(),
            PriorCells = priorCells,
            ReferencedCells = referencedCells,
        };

        return Ok(await _agent.SuggestSqlAsync(agentRequest, HttpContext.RequestAborted));
    }

    private (string companyId, string userId, bool isAdmin, string? error) ReadContext()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(companyId)) return ("", "", false, "Company ID is required");
        if (string.IsNullOrWhiteSpace(userId)) return ("", "", false, "User ID is required in headers");
        var isAdmin = User.HasCompanyRole(companyId, "ADMIN");
        return (companyId, userId, isAdmin, null);
    }
}

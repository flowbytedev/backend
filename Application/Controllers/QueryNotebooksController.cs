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

    public QueryNotebooksController(IQueryNotebookService notebooks, INotebookAgentService agent)
    {
        _notebooks = notebooks;
        _agent = agent;
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
        var (companyId, _, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");

        var (cell, cellError) = await _notebooks.AddCellAsync(companyId, id, request);
        return cell == null ? BadRequest(cellError ?? "Couldn't add the cell.") : Ok(cell);
    }

    [HttpPut("{id}/cells/{cellId}")]
    public async Task<ActionResult<NotebookCellDto>> UpdateCell(string id, string cellId, [FromBody] SaveNotebookCellRequest request)
    {
        var (companyId, _, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");

        var (cell, cellError) = await _notebooks.UpdateCellAsync(companyId, id, cellId, request, HttpContext.RequestAborted);
        return cell == null ? BadRequest(cellError ?? "Couldn't update the cell.") : Ok(cell);
    }

    [HttpDelete("{id}/cells/{cellId}")]
    public async Task<IActionResult> RemoveCell(string id, string cellId)
    {
        var (companyId, _, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return await _notebooks.RemoveCellAsync(companyId, id, cellId, HttpContext.RequestAborted) ? NoContent() : NotFound();
    }

    [HttpPut("{id}/cells/reorder")]
    public async Task<IActionResult> ReorderCells(string id, [FromBody] List<string> orderedCellIds)
    {
        var (companyId, _, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (orderedCellIds == null || orderedCellIds.Count == 0) return BadRequest("No cell order provided.");

        return await _notebooks.ReorderCellsAsync(companyId, id, orderedCellIds) ? NoContent() : NotFound();
    }

    [HttpPost("{id}/cells/{cellId}/run")]
    public async Task<ActionResult<NotebookCellRunResult>> RunCell(string id, string cellId)
    {
        var (companyId, userId, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return Ok(await _notebooks.RunCellAsync(companyId, userId, id, cellId, HttpContext.RequestAborted));
    }

    [HttpPost("{id}/run-all")]
    public async Task<ActionResult<RunAllResult>> RunAll(string id)
    {
        var (companyId, userId, _, error) = ReadContext();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return Ok(await _notebooks.RunAllAsync(companyId, userId, id, HttpContext.RequestAborted));
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

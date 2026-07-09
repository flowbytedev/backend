using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Shared.Authorization;
using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// The semantic layer for a dataset table: AI-generated + human-edited per-column documentation
/// (descriptions, display names, semantic types/units, advisory PII flags). VIEW_DATA to read;
/// EDIT_DATA to generate or save. Column structure is read live from DuckDB — this controller only
/// manages the documentation overlay.
/// </summary>
[Route("api/datasets/{datasetId}/tables/{tableName}/docs")]
[ApiController]
[Authorize(Policy = PolicyNames.DatasetsAccess)]
public class DatasetDocsController : ControllerBase
{
    private readonly IDatasetDocService _docs;
    private readonly IColumnDocGenerationService _generator;
    private readonly IDatasetService _datasetService;

    public DatasetDocsController(IDatasetDocService docs, IColumnDocGenerationService generator, IDatasetService datasetService)
    {
        _docs = docs;
        _generator = generator;
        _datasetService = datasetService;
    }

    // GET: live columns merged with saved docs.
    [HttpGet]
    public async Task<ActionResult<TableDocDto>> Get(string datasetId, string tableName)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        if (!await DatasetAccessible(datasetId, userId)) return NotFound($"Dataset '{datasetId}' not found.");

        return Ok(await _docs.GetTableDocsAsync(companyId, datasetId, tableName, HttpContext.RequestAborted));
    }

    // POST generate: run the AI generator, persist, return the updated docs.
    [HttpPost("generate")]
    public async Task<ActionResult<TableDocDto>> Generate(string datasetId, string tableName)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (!await DatasetAccessible(datasetId, userId)) return NotFound($"Dataset '{datasetId}' not found.");

        var result = await _generator.GenerateAsync(companyId, datasetId, tableName, HttpContext.RequestAborted);
        if (result.Error != null) return BadRequest(result.Error);

        return Ok(await _docs.GetTableDocsAsync(companyId, datasetId, tableName, HttpContext.RequestAborted));
    }

    // PUT: save user edits to column docs.
    [HttpPut]
    public async Task<ActionResult<TableDocDto>> Save(string datasetId, string tableName, [FromBody] List<SaveColumnDocRequest> edits)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (!await DatasetAccessible(datasetId, userId)) return NotFound($"Dataset '{datasetId}' not found.");
        if (edits == null) return BadRequest("No edits provided.");

        return Ok(await _docs.SaveColumnDocsAsync(companyId, datasetId, tableName, userId, edits, HttpContext.RequestAborted));
    }

    private (string companyId, string userId, string? error) ReadHeaders()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(companyId)) return ("", "", "Company ID is required");
        if (string.IsNullOrWhiteSpace(userId)) return ("", "", "User ID is required in headers");
        return (companyId, userId, null);
    }

    private async Task<bool> DatasetAccessible(string datasetId, string userId)
        => !string.IsNullOrWhiteSpace(datasetId) && await _datasetService.GetDatasetAsync(datasetId, userId) != null;
}

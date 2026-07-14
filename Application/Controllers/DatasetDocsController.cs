using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Shared.Authorization;
using Application.Shared.Enums;
using Application.Shared.Models;
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
[Authorize(Policy = PolicyNames.DataAdminAccess)]
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

    // GET: live columns merged with saved docs. snapshot=false (only honored for External datasets)
    // documents the live source table; snapshot=true documents the dataset's DuckDB snapshot.
    [HttpGet]
    public async Task<ActionResult<TableDocDto>> Get(string datasetId, string tableName, [FromQuery] bool snapshot = true)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "DATA_ADMIN")) return Forbid();

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null) return NotFound($"Dataset '{datasetId}' not found.");
        var snapshotMode = ResolveSnapshotMode(dataset, snapshot);

        return Ok(await _docs.GetTableDocsAsync(companyId, datasetId, tableName, snapshotMode, HttpContext.RequestAborted));
    }

    // POST generate: run the AI generator, persist, return the updated docs.
    [HttpPost("generate")]
    public async Task<ActionResult<TableDocDto>> Generate(string datasetId, string tableName, [FromQuery] bool snapshot = true)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "DATA_ADMIN")) return Forbid();

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null) return NotFound($"Dataset '{datasetId}' not found.");
        var snapshotMode = ResolveSnapshotMode(dataset, snapshot);

        var result = await _generator.GenerateAsync(companyId, datasetId, tableName, snapshotMode, HttpContext.RequestAborted);
        if (result.Error != null) return BadRequest(result.Error);

        return Ok(await _docs.GetTableDocsAsync(companyId, datasetId, tableName, snapshotMode, HttpContext.RequestAborted));
    }

    // PUT: save user edits to column docs.
    [HttpPut]
    public async Task<ActionResult<TableDocDto>> Save(string datasetId, string tableName, [FromBody] List<SaveColumnDocRequest> edits, [FromQuery] bool snapshot = true)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "DATA_ADMIN")) return Forbid();
        if (edits == null) return BadRequest("No edits provided.");

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null) return NotFound($"Dataset '{datasetId}' not found.");
        var snapshotMode = ResolveSnapshotMode(dataset, snapshot);

        return Ok(await _docs.SaveColumnDocsAsync(companyId, datasetId, tableName, snapshotMode, userId, edits, HttpContext.RequestAborted));
    }

    private (string companyId, string userId, string? error) ReadHeaders()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(companyId)) return ("", "", "Company ID is required");
        if (string.IsNullOrWhiteSpace(userId)) return ("", "", "User ID is required in headers");
        return (companyId, userId, null);
    }

    // Local datasets live only in DuckDB, so they're always documented in snapshot mode regardless of the
    // requested flag; only External datasets can be documented against their live source (snapshot=false).
    private static bool ResolveSnapshotMode(Dataset dataset, bool requested)
        => dataset.SourceType == DatasetSourceType.External ? requested : true;
}

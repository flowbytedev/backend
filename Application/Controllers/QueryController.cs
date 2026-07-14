using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Application.Shared.Authorization;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Services;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// SQL query workbench for a dataset's DuckDB tables: ad-hoc execution, write-back (save result as
/// table/view), and saved-query CRUD. Read execution is open to VIEW_DATA; write/DDL needs EDIT_DATA.
/// </summary>
[Route("api/datasets/{datasetId}")]
[ApiController]
[Authorize(Policy = PolicyNames.QueryAccess)]
public class QueryController : ControllerBase
{
    private readonly IDuckdbService _duckdbService;
    private readonly IDatasetService _datasetService;
    private readonly ISavedQueryService _savedQueryService;
    private readonly IDatabaseTableService _databaseTableService;
    private readonly IIngestionService _ingestionService;

    public QueryController(
        IDuckdbService duckdbService,
        IDatasetService datasetService,
        ISavedQueryService savedQueryService,
        IDatabaseTableService databaseTableService,
        IIngestionService ingestionService)
    {
        _duckdbService = duckdbService;
        _datasetService = datasetService;
        _savedQueryService = savedQueryService;
        _databaseTableService = databaseTableService;
        _ingestionService = ingestionService;
    }

    // POST: api/datasets/{datasetId}/query/run
    [HttpPost("query/run")]
    public async Task<ActionResult<SqlQueryResult>> Run(string datasetId, [FromBody] SqlQueryRequest request)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "QUERY")) return Forbid();
        if (string.IsNullOrWhiteSpace(request?.Sql)) return BadRequest("Query is required");

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null)
            return NotFound($"Dataset '{datasetId}' not found.");

        // Table-level share scope: block queries that reference a dataset table the user can't access.
        var blockedTable = await FindDisallowedTableAsync(dataset, userId, companyId, request.Sql, request.SnapshotMode, HttpContext.RequestAborted);
        if (blockedTable != null)
            return Ok(new SqlQueryResult { IsSelect = true, Error = $"Access to table '{blockedTable}' is not permitted." });

        // External datasets run against the live source connection (always read-only). The
        // 'snapshot' query param lets the workbench query the local DuckDB snapshots instead.
        if (dataset.SourceType == DatasetSourceType.External && !request.SnapshotMode)
        {
            var external = await _databaseTableService.ExecuteQueryAsync(
                dataset.SourceEntityId ?? "", companyId, request.Sql, request.MaxRows ?? 0, HttpContext.RequestAborted);
            return Ok(external);
        }

        // VIEW_DATA → read-only; EDIT_DATA/ADMIN → writes allowed. The service classifies the
        // statement and returns a clear error inline if a write is attempted without edit rights.
        var hasEdit = User.HasCompanyRole(companyId, "QUERY");
        var result = await _duckdbService.ExecuteSqlAsync(
            datasetId, request.Sql, allowWrite: hasEdit, maxRows: request.MaxRows ?? 0, HttpContext.RequestAborted);
        return Ok(result);
    }

    // POST: api/datasets/{datasetId}/query/save-result  — write-back as a new table or view.
    [HttpPost("query/save-result")]
    public async Task<ActionResult<SqlQueryResult>> SaveResult(string datasetId, [FromBody] SaveResultRequest request)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "QUERY")) return Forbid();
        if (request == null || string.IsNullOrWhiteSpace(request.Sql) || string.IsNullOrWhiteSpace(request.ObjectName))
            return BadRequest("Query and object name are required");

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null)
            return NotFound($"Dataset '{datasetId}' not found.");

        // Table-level share scope: the SELECT being materialized must not reference disallowed tables.
        var blockedTable = await FindDisallowedTableAsync(dataset, userId, companyId, request.Sql, request.SnapshotMode, HttpContext.RequestAborted);
        if (blockedTable != null)
            return Ok(new SqlQueryResult { Error = $"Access to table '{blockedTable}' is not permitted." });

        // External source mode: there is no write-back to a read-only source, so the result is snapshotted
        // into a local DuckDB table instead. (Snapshot mode / Local datasets fall through to DuckDB write-back.)
        if (dataset.SourceType == DatasetSourceType.External && !request.SnapshotMode)
        {
            var import = await _ingestionService.SnapshotQueryAsync(
                companyId, datasetId, dataset.SourceEntityId ?? "", request.Sql, request.ObjectName, HttpContext.RequestAborted);
            return Ok(new SqlQueryResult
            {
                IsSelect = false,
                Error = import.Error,
                RowsAffected = import.RowsInserted + import.RowsUpdated
            });
        }

        var result = await _duckdbService.CreateObjectFromQueryAsync(
            datasetId, request.ObjectName, request.Sql, request.AsView, HttpContext.RequestAborted);
        return Ok(result);
    }

    // GET: api/datasets/{datasetId}/queries
    [HttpGet("/api/datasets/{datasetId}/queries")]
    public async Task<ActionResult<IEnumerable<SavedQueryDto>>> GetSavedQueries(string datasetId)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "QUERY")) return Forbid();

        return Ok(await _savedQueryService.GetForDatasetAsync(companyId, datasetId, userId));
    }

    // POST: api/datasets/{datasetId}/queries
    [HttpPost("/api/datasets/{datasetId}/queries")]
    public async Task<ActionResult<SavedQueryDto>> CreateSavedQuery(string datasetId, [FromBody] SaveSavedQueryRequest request)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "QUERY")) return Forbid();
        if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.QueryText))
            return BadRequest("Name and query text are required");
        if (!await DatasetAccessible(datasetId, userId))
            return NotFound($"Dataset '{datasetId}' not found.");

        return Ok(await _savedQueryService.CreateAsync(companyId, datasetId, userId, request));
    }

    // PUT: api/datasets/{datasetId}/queries/{id}
    [HttpPut("/api/datasets/{datasetId}/queries/{id}")]
    public async Task<ActionResult<SavedQueryDto>> UpdateSavedQuery(string datasetId, string id, [FromBody] SaveSavedQueryRequest request)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "QUERY")) return Forbid();
        if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.QueryText))
            return BadRequest("Name and query text are required");

        var isAdmin = User.HasCompanyRole(companyId, "ADMIN");
        var updated = await _savedQueryService.UpdateAsync(companyId, id, userId, isAdmin, request);
        if (updated == null) return NotFound("Query not found, or you don't have permission to edit it.");
        return Ok(updated);
    }

    // DELETE: api/datasets/{datasetId}/queries/{id}
    [HttpDelete("/api/datasets/{datasetId}/queries/{id}")]
    public async Task<IActionResult> DeleteSavedQuery(string datasetId, string id)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "QUERY")) return Forbid();

        var isAdmin = User.HasCompanyRole(companyId, "ADMIN");
        if (!await _savedQueryService.DeleteAsync(companyId, id, userId, isAdmin))
            return NotFound("Query not found, or you don't have permission to delete it.");
        return NoContent();
    }

    // Matches the identifier following FROM/JOIN (handles quotes/brackets/backticks and schema.table).
    private static readonly System.Text.RegularExpressions.Regex TableRefRegex =
        new(@"\b(?:from|join)\s+([A-Za-z0-9_\.""\[\]`]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<string> ExtractReferencedTables(string sql)
    {
        foreach (System.Text.RegularExpressions.Match m in TableRefRegex.Matches(sql ?? ""))
        {
            var cleaned = m.Groups[1].Value.Replace("\"", "").Replace("[", "").Replace("]", "").Replace("`", "");
            if (!string.IsNullOrWhiteSpace(cleaned))
                yield return cleaned;
        }
    }

    /// <summary>
    /// Best-effort table-level guard for ad-hoc SQL: returns the name of a referenced table the user is
    /// NOT allowed to access, or null if the query is clear. Only blocks references that match a KNOWN
    /// dataset table outside the user's allow-list, so CTEs/aliases/functions are never false-flagged.
    /// </summary>
    private async Task<string?> FindDisallowedTableAsync(Dataset dataset, string userId, string companyId, string sql, bool snapshotMode, System.Threading.CancellationToken ct)
    {
        var accessible = await _datasetService.GetAccessibleTablesAsync(dataset.Id!, userId);
        if (accessible == null) return null; // full access — no guard needed

        IEnumerable<string> allTables;
        if (dataset.SourceType == DatasetSourceType.External && !snapshotMode && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
        {
            var discovery = await _databaseTableService.DiscoverTablesAsync(dataset.SourceEntityId, companyId, ct);
            allTables = discovery.Tables.Select(t => t.FullName);
        }
        else
        {
            allTables = await _duckdbService.GetTablesAsync(dataset.Id!);
        }

        var disallowed = allTables.Where(t => !accessible.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (disallowed.Count == 0) return null;

        return ExtractReferencedTables(sql).FirstOrDefault(r => disallowed.Contains(r));
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

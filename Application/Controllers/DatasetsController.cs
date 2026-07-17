using Application.Client.Pages.Data.DatasetPages;
using Application.Shared.Authorization;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Application.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = PolicyNames.DataReadAccess)]
public class DatasetsController : ControllerBase
{
    private readonly IDatasetService _datasetService;
    private readonly IDuckdbService _duckdbService;
    private readonly ISchemaInferenceService _schemaInferenceService;
    private readonly Application.Shared.Services.IDatabaseTableService _databaseTableService;
    private readonly IIngestionService _ingestionService;
    private readonly Application.Shared.Services.Org.IUserService _userService;
    private readonly IDatasetSharingService _sharingService;
    private readonly IDatasetTableMoveService _tableMoveService;
    private readonly Application.Shared.Services.Data.IUserDatasetPreferenceService _preferences;
    private readonly IDatasetDocService _docService;

    public DatasetsController(
        IDatasetService datasetService,
        IDuckdbService duckdbService,
        ISchemaInferenceService schemaInferenceService,
        Application.Shared.Services.IDatabaseTableService databaseTableService,
        IIngestionService ingestionService,
        Application.Shared.Services.Org.IUserService userService,
        IDatasetSharingService sharingService,
        IDatasetTableMoveService tableMoveService,
        Application.Shared.Services.Data.IUserDatasetPreferenceService preferences,
        IDatasetDocService docService)
    {
        _datasetService = datasetService;
        _duckdbService = duckdbService;
        _schemaInferenceService = schemaInferenceService;
        _databaseTableService = databaseTableService;
        _ingestionService = ingestionService;
        _userService = userService;
        _sharingService = sharingService;
        _tableMoveService = tableMoveService;
        _preferences = preferences;
        _docService = docService;
    }

    // GET: api/Datasets/{companyId}
    [HttpGet()]
    public async Task<ActionResult<IEnumerable<Dataset>>> GetDatasets()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault();
        var userId = Request.Headers["UserId"].ToString();
        
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var datasets = await _datasetService.GetDatasetsByCompanyAsync(companyId, userId);
        return Ok(datasets);
    }

    // GET: api/datasets/name-available?name=...&excludeId=... — is a dataset name free within the company?
    // Used by the create/edit/import forms for live "name already taken" feedback. Names are unique per
    // company (case-insensitive); pass excludeId when editing so the dataset's own name doesn't clash.
    [HttpGet("name-available")]
    public async Task<ActionResult<bool>> IsDatasetNameAvailable([FromQuery] string name, [FromQuery] string? excludeId = null)
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        return Ok(await _datasetService.IsNameAvailableAsync(companyId, name ?? string.Empty, excludeId));
    }

    // ---- Per-user pins + default dataset ----------------------------------------------------------

    private (string companyId, string userId, ActionResult? error) PreferenceHeaders()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault();
        var userId = Request.Headers["UserId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(companyId)) return ("", "", BadRequest("Company ID is required"));
        if (string.IsNullOrWhiteSpace(userId)) return ("", "", BadRequest("User ID is required in headers"));
        return (companyId, userId, null);
    }

    // GET: pinned dataset ids + the user's default dataset id.
    [HttpGet("preferences")]
    public async Task<ActionResult> GetPreferences(CancellationToken ct)
    {
        var (companyId, userId, error) = PreferenceHeaders();
        if (error != null) return error;
        var (pinned, defaultId) = await _preferences.GetDatasetPreferencesAsync(companyId, userId, ct);
        return Ok(new { pinnedDatasetIds = pinned, defaultDatasetId = defaultId });
    }

    [HttpPut("{id}/pin")]
    public async Task<ActionResult> SetDatasetPin(string id, [FromQuery] bool pinned, CancellationToken ct)
    {
        var (companyId, userId, error) = PreferenceHeaders();
        if (error != null) return error;
        await _preferences.SetDatasetPinnedAsync(companyId, userId, id, pinned, ct);
        return NoContent();
    }

    [HttpPut("{id}/default")]
    public async Task<ActionResult> SetDefaultDataset(string id, CancellationToken ct)
    {
        var (companyId, userId, error) = PreferenceHeaders();
        if (error != null) return error;
        await _preferences.SetDefaultDatasetAsync(companyId, userId, id, ct);
        return NoContent();
    }

    [HttpDelete("default")]
    public async Task<ActionResult> ClearDefaultDataset(CancellationToken ct)
    {
        var (companyId, userId, error) = PreferenceHeaders();
        if (error != null) return error;
        await _preferences.ClearDefaultDatasetAsync(companyId, userId, ct);
        return NoContent();
    }

    [HttpGet("{id}/table-pins")]
    public async Task<ActionResult<List<string>>> GetTablePins(string id, CancellationToken ct)
    {
        var (companyId, userId, error) = PreferenceHeaders();
        if (error != null) return error;
        var pins = await _preferences.GetPinnedTablesAsync(companyId, userId, id, ct);
        return Ok(pins.ToList());
    }

    [HttpPut("{id}/tables/{tableName}/pin")]
    public async Task<ActionResult> SetTablePin(string id, string tableName, [FromQuery] bool pinned, CancellationToken ct)
    {
        var (companyId, userId, error) = PreferenceHeaders();
        if (error != null) return error;
        await _preferences.SetTablePinnedAsync(companyId, userId, id, tableName, pinned, ct);
        return NoContent();
    }

    // GET: api/Datasets/item/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<Dataset>> GetDataset(string id)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        var dataset = await _datasetService.GetDatasetAsync(id, userId);
        if (dataset == null)
            return NotFound();

        return Ok(dataset);
    }

    // POST: api/Datasets
    [HttpPost]
    public async Task<ActionResult<Dataset>> CreateDataset(Dataset dataset)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            var created = await _datasetService.CreateDatasetAsync(dataset, userId);
            if (created == null)
                return Conflict("Dataset already exists.");

            return CreatedAtAction(nameof(GetDataset), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            // Business-rule violation (e.g. duplicate name) → 409 so the UI can show it as a clean conflict.
            return Conflict(ex.Message);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error creating dataset: {ex.Message}");
        }
    }

    // PUT: api/Datasets/{id}
    [HttpPut("{id}")]
    public async Task<ActionResult<Dataset>> UpdateDataset(string id, Dataset dataset)
    {
        if (id != dataset.Id)
            return BadRequest("Mismatched dataset ID");

        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            var updated = await _datasetService.UpdateDatasetAsync(id, dataset, userId);
            if (updated == null)
                return NotFound();

            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            // Business-rule violation (e.g. duplicate name) → 409 so the UI can show it as a clean conflict.
            return Conflict(ex.Message);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error updating dataset: {ex.Message}");
        }
    }

    // DELETE: api/Datasets/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteDataset(string id)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            var deleted = await _datasetService.DeleteDatasetAsync(id, userId);
            if (!deleted)
                return NotFound();

            return NoContent();
        }
        catch (Exception ex)
        {
            return BadRequest($"Error deleting dataset: {ex.Message}");
        }
    }

    // POST: api/Datasets/tables
    [HttpPost("tables")]
    public async Task<ActionResult> CreateTable(Table table)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(table.CompanyId))
            return BadRequest("Company ID is required");

        if (string.IsNullOrWhiteSpace(table.DatasetId))
            return BadRequest("Dataset ID is required");

        if (table.Columns == null || !table.Columns.Any())
            return BadRequest("At least one column is required");

        if (!await DatasetExists(table.DatasetId, userId))
            return NotFound($"Dataset with ID '{table.DatasetId}' not found.");

        // Table names must be unique within the dataset (case-insensitive). The message intentionally
        // contains "already exists" — the import wizard keys off that phrase to treat it as a re-import
        // into the existing table (append/replace/upsert) rather than a hard failure.
        var existingTables = await _duckdbService.GetTablesAsync(table.DatasetId) ?? Enumerable.Empty<string>();
        if (existingTables.Any(t => string.Equals(t, table.TableName, StringComparison.OrdinalIgnoreCase)))
            return Conflict($"A table named '{table.TableName}' already exists in this dataset.");

        await _duckdbService.CreateTableAsync(table);


        return CreatedAtAction(nameof(GetTable), new { datasetId = table.DatasetId, tableName = table.TableName }, table);
    }

    // GET: api/datasets/{datasetId}/tables/{tableName}/exists — is this table name already used in the
    // dataset (case-insensitive)? Used by the Create Table form for live "name already taken" feedback.
    [HttpGet("{datasetId}/tables/{tableName}/exists")]
    public async Task<ActionResult<bool>> TableExists(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        var tables = await _duckdbService.GetTablesAsync(datasetId) ?? Enumerable.Empty<string>();
        return Ok(tables.Any(t => string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase)));
    }

    // POST: api/Datasets/infer-schema
    // AI-assisted column data type inference for the import "Configure Schema" step.
    [HttpPost("infer-schema")]
    public async Task<ActionResult<SchemaInferenceResult>> InferSchema([FromBody] SchemaInferenceRequest request)
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (request?.Columns == null || request.Columns.Count == 0)
            return BadRequest("At least one column is required");

        var result = await _schemaInferenceService.InferColumnTypesAsync(request, HttpContext.RequestAborted);
        return Ok(result);
    }

    // GET: api/Datasets/{datasetId}/tables
    [HttpGet("{datasetId}/tables")]
    public async Task<ActionResult<IEnumerable<string>>> GetTables(string datasetId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");        

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        var tables = await _duckdbService.GetTablesAsync(datasetId);

        if (tables == null || !tables.Any())
            return NotFound("No tables found in the dataset.");

        // Restrict to the tables this user is scoped to (null = all).
        var allowed = await _datasetService.GetAccessibleTablesAsync(datasetId, userId);
        if (allowed != null)
            tables = tables.Where(t => allowed.Contains(t)).ToList();

        if (!tables.Any())
            return NotFound("No tables found in the dataset.");

        return Ok(tables);
    }

    // GET: api/Datasets/{datasetId}/tables/columns — columns for every accessible table in one round trip,
    // so callers like the notebook's SQL autocomplete don't have to issue one request per table.
    [HttpGet("{datasetId}/tables/columns")]
    public async Task<ActionResult<Dictionary<string, List<Column>>>> GetAllTableColumns(string datasetId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        var tables = await _duckdbService.GetTablesAsync(datasetId) ?? new List<string>();

        var allowed = await _datasetService.GetAccessibleTablesAsync(datasetId, userId);
        if (allowed != null)
            tables = tables.Where(t => allowed.Contains(t)).ToList();

        var result = new Dictionary<string, List<Column>>();
        foreach (var table in tables)
        {
            try { result[table] = await _duckdbService.GetTableColumnsAsync(datasetId, table); }
            catch { /* skip tables whose columns can't be read rather than failing the whole map */ }
        }

        return Ok(result);
    }

    // GET: api/Datasets/stats — per-dataset annotations (table count, file size, row estimate, status)
    // for the datasets list page. One entry per dataset the user can see.
    [HttpGet("stats")]
    public async Task<ActionResult<IEnumerable<DatasetStats>>> GetDatasetsStats()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault();
        var userId = Request.Headers["UserId"].ToString();

        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var datasets = await _datasetService.GetDatasetsByCompanyAsync(companyId, userId);

        var shareCounts = await _sharingService.GetDatasetShareCountsAsync(
            datasets.Where(d => !string.IsNullOrEmpty(d.Id)).Select(d => d.Id!), HttpContext.RequestAborted);

        var stats = new List<DatasetStats>();
        foreach (var ds in datasets)
        {
            if (string.IsNullOrEmpty(ds.Id))
                continue;

            var exists = _duckdbService.DatabaseExists(ds.Id);
            var size = _duckdbService.GetDatabaseFileSize(ds.Id);
            var (tableCount, totalRows) = exists
                ? await _duckdbService.GetDatasetTableSummaryAsync(ds.Id, HttpContext.RequestAborted)
                : (0, 0L);

            var status = ds.SourceType == Application.Shared.Enums.DatasetSourceType.External ? "External"
                : !exists ? "No database"
                : tableCount == 0 ? "Empty"
                : "In use";

            stats.Add(new DatasetStats
            {
                DatasetId = ds.Id,
                DatabaseExists = exists,
                TableCount = tableCount,
                SizeBytes = size,
                TotalRows = totalRows,
                Status = status,
                SharedWith = shareCounts.TryGetValue(ds.Id, out var sc) ? sc : 0
            });
        }

        return Ok(stats);
    }

    // GET: api/Datasets/{datasetId}/tables/stats — row count, column count and estimated size per table.
    [HttpGet("{datasetId}/tables/stats")]
    public async Task<ActionResult<IEnumerable<TableStats>>> GetTableStats(string datasetId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        var stats = await _duckdbService.GetTableStatsAsync(datasetId, HttpContext.RequestAborted);

        // Respect table-level share scope (a null accessible-set means all tables).
        var allowed = await _datasetService.GetAccessibleTablesAsync(datasetId, userId);
        if (allowed != null)
            stats = stats.Where(s => allowed.Contains(s.TableName)).ToList();

        // Enrich with ingestion + owner metadata. A table fed by a scheduled ingestion source takes its
        // owner/created + last-sync info from that source; otherwise the owner falls back to the dataset
        // creator (DuckDB itself tracks no per-table owner or creation date).
        var sources = await _ingestionService.GetSourcesAsync(companyId, datasetId, HttpContext.RequestAborted);
        var byTable = new Dictionary<string, IngestionSourceDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
            byTable[s.TargetTable] = s; // typically one source per target table

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        foreach (var st in stats)
        {
            if (byTable.TryGetValue(st.TableName, out var src))
            {
                st.IsIngested = true;
                st.IngestionEnabled = src.IsEnabled;
                st.LastSyncedAt = src.LastRunAt;
                st.LastRunStatus = src.LastRunStatus;
                st.LastRunRows = src.LastRunRows;
                st.Owner = src.CreatedBy;
                st.CreatedAt = src.CreatedAt;
            }
            else
            {
                st.Owner = dataset?.CreatedBy;
            }
        }

        // Resolve owner user-ids (Azure AD object ids) to emails, so the UI shows a person, not a guid.
        // Same resolution path used by dataset sharing (IUserService.GetUser). Unknown ids keep their raw
        // value as a fallback.
        var ownerIds = stats.Select(s => s.Owner)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct()
            .ToList();
        var emailById = new Dictionary<string, string>();
        foreach (var id in ownerIds)
        {
            try
            {
                var user = await _userService.GetUser(id!);
                if (!string.IsNullOrWhiteSpace(user?.Email))
                    emailById[id!] = user!.Email!;
            }
            catch { /* leave unresolved ids as-is */ }
        }
        foreach (var st in stats)
            if (!string.IsNullOrWhiteSpace(st.Owner) && emailById.TryGetValue(st.Owner!, out var email))
                st.Owner = email;

        // How many users each table is shared with (full-dataset + table-scoped shares).
        var shareCounts = await _sharingService.GetTableShareCountsAsync(
            datasetId, stats.Select(s => s.TableName), HttpContext.RequestAborted);
        foreach (var st in stats)
            if (shareCounts.TryGetValue(st.TableName, out var sc))
                st.SharedWith = sc;

        return Ok(stats);
    }

    // GET: api/Datasets/{datasetId}/database-exists — does the dataset's DuckDB file exist on disk?
    [HttpGet("{datasetId}/database-exists")]
    public async Task<ActionResult<bool>> DatabaseExists(string datasetId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        return Ok(_duckdbService.DatabaseExists(datasetId));
    }

    // POST: api/Datasets/{datasetId}/database — create the dataset's DuckDB file if it is missing.
    [HttpPost("{datasetId}/database")]
    public async Task<IActionResult> CreateDatabase(string datasetId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        try
        {
            await _duckdbService.EnsureDatabaseAsync(datasetId);
            return Ok();
        }
        catch (Exception ex)
        {
            return BadRequest($"Error creating database: {ex.Message}");
        }
    }


    // GET: api/datasets/source-databases — Database-type entities (with a saved connection) a dataset can be backed by.
    [HttpGet("source-databases")]
    public async Task<ActionResult<IEnumerable<DatabaseEntityOptionDto>>> GetSourceDatabases(CancellationToken ct)
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(companyId))
            return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN") && !User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        return Ok(await _databaseTableService.GetConnectedDatabasesAsync(companyId, ct));
    }

    // GET: api/datasets/{datasetId}/source-tables — the live tables of an External dataset's source database.
    [HttpGet("{datasetId}/source-tables")]
    public async Task<ActionResult<IEnumerable<string>>> GetSourceTables(string datasetId, CancellationToken ct)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        // Viewing an external database's live source tables is restricted to data administrators
        // ({companyId}_DATA_ADMIN) and company admins ({companyId}_ADMIN, implicitly allowed by
        // HasCompanyRole). Other data roles (VIEW_DATA/QUERY) only ever see the local DuckDB snapshot.
        if (!User.HasCompanyRole(companyId, "DATA_ADMIN"))
            return Forbid();

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null)
            return NotFound($"Dataset with ID '{datasetId}' not found.");
        if (dataset.SourceType != Application.Shared.Enums.DatasetSourceType.External || string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            return BadRequest("This dataset is not backed by an external database.");

        var discovery = await _databaseTableService.DiscoverTablesAsync(dataset.SourceEntityId, companyId, ct);
        // Surface listing failures as a 400 so the workbench can show why no tables appeared.
        if (!string.IsNullOrEmpty(discovery.Error))
            return BadRequest(discovery.Error);

        var names = discovery.Tables.Select(t => t.FullName);

        // Restrict to the tables this user is scoped to (null = all).
        var allowed = await _datasetService.GetAccessibleTablesAsync(datasetId, userId);
        if (allowed != null)
            names = names.Where(n => allowed.Contains(n));

        return Ok(names.ToList());
    }

    // GET: api/datasets/{datasetId}/documented-tables?snapshot=true — names of tables that already have
    // saved column docs for the given layer (snapshot = DuckDB, false = live source), so table lists can
    // badge which tables are documented. snapshot is ignored (treated as true) for Local datasets.
    [HttpGet("{datasetId}/documented-tables")]
    public async Task<ActionResult<IEnumerable<string>>> GetDocumentedTables(string datasetId, [FromQuery] bool snapshot = true)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null)
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        // Local datasets only ever have snapshot docs; only External datasets can be documented live.
        var snapshotMode = dataset.SourceType == Application.Shared.Enums.DatasetSourceType.External ? snapshot : true;
        var names = await _docService.GetDocumentedTablesAsync(companyId, datasetId, snapshotMode, HttpContext.RequestAborted);
        return Ok(names);
    }

    // GET: api/Datasets/{datasetId}/tables
    [HttpGet("{datasetId}/tables/{tableName}")]
    public async Task<ActionResult<Table>> GetTable(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();

        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        if (!await IsTableAllowedAsync(datasetId, userId, tableName))
            return Forbid();

        return await _duckdbService.GetTableAsync(datasetId, tableName);

    }

    // get table columns
    // GET: api/Datasets/{datasetId}/tables/{tableName}/columns
    [HttpGet("{datasetId}/tables/{tableName}/columns")]
    public async Task<ActionResult<IEnumerable<Column>>> GetColumns(string datasetId, string tableName)
    {
        //var userId = Request.Headers["UserId"].ToString();
        //if (string.IsNullOrWhiteSpace(userId))
        //    return BadRequest("User ID is required in headers");

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required"); 
        
        try
        {
            var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
            if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
                return Forbid();

            var userId = Request.Headers["UserId"].ToString();
            if (!string.IsNullOrWhiteSpace(userId) && !await IsTableAllowedAsync(datasetId, userId, tableName))
                return Forbid();

            // Prefer the DuckDB (local/snapshot) columns. For an External dataset whose live source table
            // has no local snapshot, DuckDB returns nothing — fall back to reading the source schema live.
            List<Column> columns;
            try { columns = await _duckdbService.GetTableColumnsAsync(datasetId, tableName); }
            catch { columns = new List<Column>(); }

            if (columns == null || columns.Count == 0)
            {
                var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
                if (dataset?.SourceType == Application.Shared.Enums.DatasetSourceType.External
                    && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
                {
                    var schema = await _databaseTableService.GetTableSchemaAsync(
                        dataset.SourceEntityId!, companyId, tableName, HttpContext.RequestAborted);
                    if (string.IsNullOrEmpty(schema.Error))
                        columns = schema.Columns;
                }
            }

            return Ok(columns ?? new List<Column>());
        }
        catch (Exception ex)
        {
            return BadRequest($"Error retrieving columns: {ex.Message}");
        }
    }


    // DELETE: api/Datasets/{datasetId}/tables/{tableName}
    [HttpDelete("{datasetId}/tables/{tableName}")]
    public async Task<ActionResult<Table>> DeleteTable(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (string.IsNullOrWhiteSpace(tableName))
            return BadRequest("Table name is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");


        if (!await IsTableAllowedAsync(datasetId, userId, tableName))
            return Forbid();

        var response = await _duckdbService.DeleteTableAsync(datasetId, tableName);

        if (!response)
            return NotFound($"Table '{tableName}' not found in dataset '{datasetId}'.");

        return NoContent();

    }

    // POST: api/Datasets/{datasetId}/tables/{tableName}/move — move a table's data to another dataset,
    // re-pointing any table-scoped sharing and notifying affected users by email.
    [HttpPost("{datasetId}/tables/{tableName}/move")]
    public async Task<ActionResult<MoveTableOutcome>> MoveTable(string datasetId, string tableName, [FromBody] MoveTableRequest request)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (string.IsNullOrWhiteSpace(tableName))
            return BadRequest("Table name is required");

        if (string.IsNullOrWhiteSpace(request?.TargetDatasetId))
            return BadRequest("Target dataset ID is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        if (!await IsTableAllowedAsync(datasetId, userId, tableName))
            return Forbid();

        // The destination must also belong to this company and be reachable by the user.
        if (!await DatasetExists(request.TargetDatasetId, userId))
            return NotFound($"Dataset with ID '{request.TargetDatasetId}' not found.");

        var movedBy = await _userService.GetUser(userId);
        var movedByName = movedBy?.UserName ?? movedBy?.Email ?? "Someone";

        var outcome = await _tableMoveService.MoveTableAsync(companyId, datasetId, tableName, request.TargetDatasetId, userId, movedByName, HttpContext.RequestAborted);
        if (!outcome.Success)
            return BadRequest(outcome.Error);

        return Ok(outcome);
    }

    // GET: api/Datasets/{datasetId}/tables/{tableName}/download
    [HttpGet("{datasetId}/tables/{tableName}/download")]
    public async Task<ActionResult> DownloadTable(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (string.IsNullOrWhiteSpace(tableName))
            return BadRequest("Table name is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        try
        {
            if (!await IsTableAllowedAsync(datasetId, userId, tableName))
                return Forbid();

            // Create a query to get all table data (no pagination for download)
            var query = new TableDataQuery
            {
                DatasetId = datasetId,
                TableName = tableName,
                Page = 1,
                PageSize = int.MaxValue // Get all data for download
            };

            var tableDataResult = await _duckdbService.QueryTableDataAsync(query);
            
            if (tableDataResult?.Data == null || !tableDataResult.Data.Any())
                return NotFound($"No data found for table '{tableName}' in dataset '{datasetId}'.");

            // Convert data to CSV format
            var csvContent = new StringBuilder();
            
            // Add headers from columns if available, otherwise from first data row
            if (tableDataResult.Columns?.Any() == true)
            {
                var headers = string.Join(",", tableDataResult.Columns.Select(c => $"\"{c.Name}\""));
                csvContent.AppendLine(headers);
            }
            else if (tableDataResult.Data.Any())
            {
                var firstRow = tableDataResult.Data.First();
                var headers = string.Join(",", firstRow.Keys.Select(k => $"\"{k}\""));
                csvContent.AppendLine(headers);
            }
            
            // Add data rows
            foreach (var row in tableDataResult.Data)
            {
                var values = string.Join(",", row.Values.Select(v => $"\"{v?.ToString()?.Replace("\"", "\"\"") ?? ""}\""));
                csvContent.AppendLine(values);
            }

            var csvBytes = Encoding.UTF8.GetBytes(csvContent.ToString());
            var fileName = $"{tableName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            return File(csvBytes, "text/csv", fileName);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error downloading table data: {ex.Message}");
        }
    }

    // POST: api/Datasets/{datasetId}/tables/{tableName}/download-filtered
    [HttpPost("{datasetId}/tables/{tableName}/download-filtered")]
    public async Task<ActionResult> DownloadFilteredTable(string datasetId, string tableName, [FromBody] TableDataQuery query)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (string.IsNullOrWhiteSpace(tableName))
            return BadRequest("Table name is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        try
        {
            if (!await IsTableAllowedAsync(datasetId, userId, tableName))
                return Forbid();

            // Ensure the query has the correct dataset and table information
            query.DatasetId = datasetId;
            query.TableName = tableName;
            
            // Set page size to max to get all filtered data
            query.PageSize = int.MaxValue;
            query.Page = 1;

            var tableDataResult = await _duckdbService.QueryTableDataAsync(query);
            
            if (tableDataResult?.Data == null || !tableDataResult.Data.Any())
                return NotFound($"No data found for table '{tableName}' with the applied filters.");

            // Convert data to CSV format
            var csvContent = new StringBuilder();
            
            // Add headers - use selected columns if specified, otherwise use all columns from result
            List<string> columnHeaders;
            if (query.SelectedColumns?.Any() == true)
            {
                columnHeaders = query.SelectedColumns;
            }
            else if (tableDataResult.Columns?.Any() == true)
            {
                columnHeaders = tableDataResult.Columns.Select(c => c.Name).ToList();
            }
            else if (tableDataResult.Data.Any())
            {
                columnHeaders = tableDataResult.Data.First().Keys.ToList();
            }
            else
            {
                return BadRequest("No columns available for export.");
            }

            // Add CSV headers
            var headers = string.Join(",", columnHeaders.Select(h => $"\"{h}\""));
            csvContent.AppendLine(headers);
            
            // Add data rows - only include selected columns
            foreach (var row in tableDataResult.Data)
            {
                var values = columnHeaders.Select(col => 
                {
                    var value = row.ContainsKey(col) ? row[col] : "";
                    return $"\"{value?.ToString()?.Replace("\"", "\"\"") ?? ""}\"";
                });
                csvContent.AppendLine(string.Join(",", values));
            }

            var csvBytes = Encoding.UTF8.GetBytes(csvContent.ToString());
            
            // Create descriptive filename
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var hasFilters = query.Filters?.Any() == true;
            var hasSelectedColumns = query.SelectedColumns?.Any() == true;
            
            string fileName;
            if (hasFilters && hasSelectedColumns)
            {
                fileName = $"{tableName}_filtered_custom_columns_{timestamp}.csv";
            }
            else if (hasFilters)
            {
                fileName = $"{tableName}_filtered_{timestamp}.csv";
            }
            else if (hasSelectedColumns)
            {
                fileName = $"{tableName}_custom_columns_{timestamp}.csv";
            }
            else
            {
                fileName = $"{tableName}_{timestamp}.csv";
            }

            return File(csvBytes, "text/csv", fileName);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error downloading filtered table data: {ex.Message}");
        }
    }

    // POST: api/Datasets/import-csv
    [HttpPost("import-csv")]
    [RequestSizeLimit(300_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300_000_000)]
    public async Task<ActionResult> ImportCsvData()
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        try
        {
            // Check if the request contains multipart/form-data
            if (!Request.HasFormContentType)
                return BadRequest("Request must be multipart/form-data");

            var form = await Request.ReadFormAsync();
            
            // Get the required form data
            if (!form.TryGetValue("datasetId", out var datasetIdValues) || 
                string.IsNullOrWhiteSpace(datasetIdValues.FirstOrDefault()))
                return BadRequest("Dataset ID is required");

            if (!form.TryGetValue("tableName", out var tableNameValues) || 
                string.IsNullOrWhiteSpace(tableNameValues.FirstOrDefault()))
                return BadRequest("Table name is required");

            var datasetId = datasetIdValues.First()!;
            var tableName = tableNameValues.First()!;

            // Get the CSV file
            if (form.Files.Count == 0)
                return BadRequest("CSV file is required");

            var csvFile = form.Files[0];
            if (csvFile.Length == 0)
                return BadRequest("CSV file cannot be empty");

            // Validate file type
            if (!csvFile.ContentType.StartsWith("text/csv") && 
                !Path.GetExtension(csvFile.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                return BadRequest("File must be a CSV file");

            // Check if dataset exists
            if (!await DatasetExists(datasetId, userId))
                return NotFound($"Dataset with ID '{datasetId}' not found.");

            // Import the CSV data
            using var csvStream = csvFile.OpenReadStream();
            var success = await _duckdbService.ImportCsvDataAsync(datasetId, tableName, csvStream);

            if (!success)
                return StatusCode(500, "Failed to import CSV data");

            return Ok(new { message = "CSV data imported successfully", datasetId, tableName });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error importing CSV data: {ex.Message}");
        }
    }

    // POST: api/Datasets/import-csv
    [HttpPost("external/import-csv")]
    public async Task<ActionResult> ImportExternalCsvData()
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        try
        {
            // Check if the request contains multipart/form-data
            if (!Request.HasFormContentType)
                return BadRequest("Request must be multipart/form-data");

            var form = await Request.ReadFormAsync();

            // Get the required form data
            if (!form.TryGetValue("datasetId", out var datasetIdValues) ||
                string.IsNullOrWhiteSpace(datasetIdValues.FirstOrDefault()))
                return BadRequest("Dataset ID is required");

            if (!form.TryGetValue("tableName", out var tableNameValues) ||
                string.IsNullOrWhiteSpace(tableNameValues.FirstOrDefault()))
                return BadRequest("Table name is required");

            if (!form.TryGetValue("companyId", out var companyIdValues) ||
                string.IsNullOrWhiteSpace(tableNameValues.FirstOrDefault()))
                return BadRequest("Company Id is required");

            var datasetId = datasetIdValues.First()!;
            var tableName = tableNameValues.First()!;
            var companyId = companyIdValues.First()!;


            // Get the CSV file
            if (form.Files.Count == 0)
                return BadRequest("CSV file is required");

            var csvFile = form.Files[0];
            if (csvFile.Length == 0)
                return BadRequest("CSV file cannot be empty");

            // Validate file type
            if (!csvFile.ContentType.StartsWith("text/csv") &&
                !Path.GetExtension(csvFile.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                return BadRequest("File must be a CSV file");

            
            // If the dataset does not exist, create it
            //var dataset = new Dataset
            //{
            //    Id = datasetId,
            //    Name = tableName, // You can set a more meaningful name
            //    CompanyId = companyId, // Set a default company ID or pass it as a parameter
            //    Description = "Imported dataset from CSV",
            //};
            //await _duckdbService.CreateDatabaseAsync(dataset);
            

            // Check if dataset exists
            //if (!await DatasetExists(datasetId))
            //    return NotFound($"Dataset with ID '{datasetId}' not found.");

            // Import the CSV data
            using var csvStream = csvFile.OpenReadStream();
            var success = await _duckdbService.ImportCsvDataAsync(companyId, datasetId, tableName, csvStream, createDataset: true, createTable: true);

            if (!success)
                return StatusCode(500, "Failed to import CSV data");

            return Ok(new { message = "CSV data imported successfully", datasetId, tableName });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error importing CSV data: {ex.Message}");
        }
    }

    // POST: api/Datasets/{datasetId}/peek-file
    // Stages an uploaded file (no target table) and returns DuckDB-inferred columns + a preview, so the
    // wizard can populate the schema editor for formats the browser can't parse (JSON/Parquet/Excel).
    [HttpPost("{datasetId}/peek-file")]
    [RequestSizeLimit(300_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300_000_000)]
    public async Task<ActionResult<FilePeekResult>> PeekFile(string datasetId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (!Request.HasFormContentType)
            return BadRequest("Request must be multipart/form-data");

        var form = await Request.ReadFormAsync();
        if (form.Files.Count == 0 || form.Files[0].Length == 0)
            return BadRequest("A non-empty file is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        var format = ParseFormat(form);
        using var stream = form.Files[0].OpenReadStream();
        var result = await _duckdbService.PeekFileAsync(datasetId, stream, format, HttpContext.RequestAborted);
        return Ok(result);
    }

    // POST: api/Datasets/{datasetId}/tables/{tableName}/validate-import
    // Stages the uploaded file and validates it against the target table schema (no commit).
    [HttpPost("{datasetId}/tables/{tableName}/validate-import")]
    [RequestSizeLimit(300_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300_000_000)]
    public async Task<ActionResult<ImportValidationResult>> ValidateImport(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (!Request.HasFormContentType)
            return BadRequest("Request must be multipart/form-data");

        var form = await Request.ReadFormAsync();
        if (form.Files.Count == 0 || form.Files[0].Length == 0)
            return BadRequest("A non-empty file is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        var format = ParseFormat(form);
        using var stream = form.Files[0].OpenReadStream();
        if (!await IsTableAllowedAsync(datasetId, userId, tableName))
            return Forbid();

        var result = await _duckdbService.ValidateImportAsync(datasetId, tableName, stream, format, HttpContext.RequestAborted);
        return Ok(result);
    }

    // POST: api/Datasets/{datasetId}/validate-schema
    // Validates an uploaded file against a caller-supplied schema (the columns being defined in the
    // wizard), before the table exists. Form fields: file, format, columns (JSON array of {name,dataType}).
    [HttpPost("{datasetId}/validate-schema")]
    [RequestSizeLimit(300_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300_000_000)]
    public async Task<ActionResult<ImportValidationResult>> ValidateSchema(string datasetId)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (!Request.HasFormContentType)
            return BadRequest("Request must be multipart/form-data");

        var form = await Request.ReadFormAsync();
        if (form.Files.Count == 0 || form.Files[0].Length == 0)
            return BadRequest("A non-empty file is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        if (!form.TryGetValue("columns", out var columnsJson) || string.IsNullOrWhiteSpace(columnsJson.FirstOrDefault()))
            return BadRequest("Columns are required");

        List<Column>? columns;
        try
        {
            columns = System.Text.Json.JsonSerializer.Deserialize<List<Column>>(columnsJson.First()!,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return BadRequest($"Invalid columns payload: {ex.Message}");
        }
        if (columns == null || columns.Count == 0)
            return BadRequest("At least one column is required");

        var format = ParseFormat(form);
        using var stream = form.Files[0].OpenReadStream();
        var result = await _duckdbService.ValidateImportAgainstSchemaAsync(datasetId, columns, stream, format, HttpContext.RequestAborted);
        return Ok(result);
    }

    // POST: api/Datasets/{datasetId}/tables/{tableName}/import
    // Commits the uploaded file into the target table using the chosen mode.
    [HttpPost("{datasetId}/tables/{tableName}/import")]
    [RequestSizeLimit(300_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300_000_000)]
    public async Task<ActionResult<ImportResult>> ImportFile(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return Forbid();

        if (!Request.HasFormContentType)
            return BadRequest("Request must be multipart/form-data");

        var form = await Request.ReadFormAsync();
        if (form.Files.Count == 0 || form.Files[0].Length == 0)
            return BadRequest("A non-empty file is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        var format = ParseFormat(form);

        var mode = ImportMode.Append;
        if (form.TryGetValue("mode", out var modeValues))
            Enum.TryParse(modeValues.FirstOrDefault(), ignoreCase: true, out mode);

        var keyColumns = form.TryGetValue("keyColumns", out var keyValues)
            ? (keyValues.FirstOrDefault() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        var skipInvalidRows = form.TryGetValue("skipInvalidRows", out var skipValues)
            && bool.TryParse(skipValues.FirstOrDefault(), out var skip) && skip;

        if (!await IsTableAllowedAsync(datasetId, userId, tableName))
            return Forbid();

        using var stream = form.Files[0].OpenReadStream();
        // The wizard creates the table before importing, so the table already exists here.
        var result = await _duckdbService.ImportFileAsync(datasetId, tableName, stream, format, mode, keyColumns, skipInvalidRows, createIfMissing: false, HttpContext.RequestAborted);
        return Ok(result);
    }

    // Reads the optional "format" form field; defaults to CSV.
    private static ImportFileFormat ParseFormat(Microsoft.AspNetCore.Http.IFormCollection form)
    {
        var format = ImportFileFormat.Csv;
        if (form.TryGetValue("format", out var formatValues))
            Enum.TryParse(formatValues.FirstOrDefault(), ignoreCase: true, out format);
        return format;
    }

    // GET: api/Datasets/{datasetId}/tables/{tableName}/data
    [HttpPost("{datasetId}/tables/{tableName}/data")]
    public async Task<ActionResult<TableDataResult>> GetTableData(string datasetId, string tableName, [FromBody] TableDataQuery query)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (string.IsNullOrWhiteSpace(tableName))
            return BadRequest("Table name is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        try
        {
            if (!await IsTableAllowedAsync(datasetId, userId, tableName))
                return Forbid();

            query.DatasetId = datasetId;
            query.TableName = tableName;

            var result = await _duckdbService.QueryTableDataAsync(query);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error querying table data: {ex.Message}");
        }
    }

    // GET: api/Datasets/{datasetId}/tables/{tableName}/count
    [HttpPost("{datasetId}/tables/{tableName}/count")]
    public async Task<ActionResult<int>> GetTableRowCount(string datasetId, string tableName, [FromBody] List<FilterCondition>? filters = null)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "VIEW_DATA", "QUERY", "DATA_ADMIN"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(datasetId))
            return BadRequest("Dataset ID is required");

        if (string.IsNullOrWhiteSpace(tableName))
            return BadRequest("Table name is required");

        if (!await DatasetExists(datasetId, userId))
            return NotFound($"Dataset with ID '{datasetId}' not found.");

        try
        {
            if (!await IsTableAllowedAsync(datasetId, userId, tableName))
                return Forbid();

            var count = await _duckdbService.GetTableRowCountAsync(datasetId, tableName, filters);
            return Ok(count);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error getting table row count: {ex.Message}");
        }
    }

    // PUT: api/Datasets/{datasetId}/tables/{tableName}/rows — update one row (addressed by its rowid).
    [HttpPut("{datasetId}/tables/{tableName}/rows")]
    public async Task<ActionResult<RowMutationResult>> UpdateRow(string datasetId, string tableName, [FromBody] RowEditModel request)
    {
        var (error, _) = await EnsureRowEditAsync(datasetId, tableName);
        if (error != null) return error;
        if (request?.RowId == null)
            return BadRequest("Row identifier is required to update a row.");

        var result = await _duckdbService.UpdateRowAsync(datasetId, tableName, request.RowId.Value, request.Values ?? new(), HttpContext.RequestAborted);
        return Ok(result);
    }

    // POST: api/Datasets/{datasetId}/tables/{tableName}/rows — insert a new row.
    [HttpPost("{datasetId}/tables/{tableName}/rows")]
    public async Task<ActionResult<RowMutationResult>> InsertRow(string datasetId, string tableName, [FromBody] RowEditModel request)
    {
        var (error, _) = await EnsureRowEditAsync(datasetId, tableName);
        if (error != null) return error;
        if (request?.Values == null || request.Values.Count == 0)
            return BadRequest("At least one column value is required to insert a row.");

        var result = await _duckdbService.InsertRowAsync(datasetId, tableName, request.Values, HttpContext.RequestAborted);
        return Ok(result);
    }

    // DELETE: api/Datasets/{datasetId}/tables/{tableName}/rows/{rowId} — delete one row (by rowid).
    [HttpDelete("{datasetId}/tables/{tableName}/rows/{rowId:long}")]
    public async Task<ActionResult<RowMutationResult>> DeleteRow(string datasetId, string tableName, long rowId)
    {
        var (error, _) = await EnsureRowEditAsync(datasetId, tableName);
        if (error != null) return error;

        var result = await _duckdbService.DeleteRowAsync(datasetId, tableName, rowId, HttpContext.RequestAborted);
        return Ok(result);
    }

    // POST: api/Datasets/{datasetId}/tables/{tableName}/rows/bulk — apply a batch of edits atomically.
    [HttpPost("{datasetId}/tables/{tableName}/rows/bulk")]
    public async Task<ActionResult<BulkRowEditResult>> BulkEditRows(string datasetId, string tableName, [FromBody] BulkRowEditRequest request)
    {
        var (error, _) = await EnsureRowEditAsync(datasetId, tableName);
        if (error != null) return error;
        if (request == null)
            return BadRequest("No changes provided.");

        var result = await _duckdbService.ApplyRowChangesAsync(datasetId, tableName, request, HttpContext.RequestAborted);
        return Ok(result);
    }

    // Shared guard for row edits: requires EDIT_DATA, a local (non-external) dataset the user can reach,
    // and table-share access. Returns a non-null error result to short-circuit, otherwise the dataset.
    private async Task<(ActionResult? error, Dataset? dataset)> EnsureRowEditAsync(string datasetId, string tableName)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return (BadRequest("User ID is required in headers"), null);

        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        if (!User.HasCompanyRole(companyId, "EDIT_DATA"))
            return (Forbid(), null);

        if (string.IsNullOrWhiteSpace(tableName))
            return (BadRequest("Table name is required"), null);

        var dataset = await _datasetService.GetDatasetAsync(datasetId, userId);
        if (dataset == null)
            return (NotFound($"Dataset with ID '{datasetId}' not found."), null);

        // External datasets are read-only snapshots of a source database — editing them is not supported.
        if (dataset.SourceType == Application.Shared.Enums.DatasetSourceType.External)
            return (BadRequest("This dataset is backed by an external database and is read-only."), null);

        if (!await IsTableAllowedAsync(datasetId, userId, tableName))
            return (Forbid(), null);

        return (null, dataset);
    }

    private async Task<bool> DatasetExists(string id, string userId)
    {
        return await _datasetService.GetDatasetAsync(id, userId) != null;
    }

    /// <summary>True when the user may access this specific table (table-level share scope).
    /// A null accessible-set means full access to all tables.</summary>
    private async Task<bool> IsTableAllowedAsync(string datasetId, string userId, string tableName)
    {
        var allowed = await _datasetService.GetAccessibleTablesAsync(datasetId, userId);
        return allowed == null || allowed.Contains(tableName);
    }

}

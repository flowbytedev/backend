using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models.Data;
using Application.Shared.Models.Notebooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services.Data;

public interface IQueryNotebookService
{
    Task<List<QueryNotebookDto>> GetForCompanyAsync(string companyId, string userId, bool isAdmin);
    Task<QueryNotebookDto?> GetAsync(string companyId, string id, string userId, bool isAdmin);
    Task<QueryNotebookDto> CreateAsync(string companyId, string userId, SaveNotebookRequest request);
    Task<QueryNotebookDto?> RenameAsync(string companyId, string id, string userId, bool isAdmin, SaveNotebookRequest request);
    Task<bool> DeleteAsync(string companyId, string id, string userId, bool isAdmin, CancellationToken ct = default);

    Task<(NotebookCellDto? Cell, string? Error)> AddCellAsync(string companyId, string userId, bool isAdmin, string notebookId, SaveNotebookCellRequest request);
    Task<(NotebookCellDto? Cell, string? Error)> UpdateCellAsync(string companyId, string userId, bool isAdmin, string notebookId, string cellId, SaveNotebookCellRequest request, CancellationToken ct = default);
    Task<bool> RemoveCellAsync(string companyId, string userId, bool isAdmin, string notebookId, string cellId, CancellationToken ct = default);
    Task<bool> ReorderCellsAsync(string companyId, string userId, bool isAdmin, string notebookId, List<string> orderedCellIds);

    Task<NotebookCellRunResult> RunCellAsync(string companyId, string userId, bool isAdmin, string notebookId, string cellId, Dictionary<string, string>? parameters = null, string triggeredBy = "manual", CancellationToken ct = default);
    Task<RunAllResult> RunAllAsync(string companyId, string userId, bool isAdmin, string notebookId, Dictionary<string, string>? parameters = null, string triggeredBy = "run_all", CancellationToken ct = default);

    Task<QueryNotebookDto?> DuplicateAsync(string companyId, string id, string userId, bool isAdmin, string? newName, CancellationToken ct = default);
    Task<NotebookExportDto?> ExportAsync(string companyId, string id, string userId, bool isAdmin);
    Task<QueryNotebookDto> ImportAsync(string companyId, string userId, NotebookExportDto export);

    Task<QueryNotebookDto?> UpdateScheduleAsync(string companyId, string notebookId, string userId, bool isAdmin, ScheduleNotebookRequest request);
    Task<List<NotebookCellRunDto>> GetCellRunHistoryAsync(string companyId, string notebookId, string cellId, string userId, bool isAdmin, int take = 20);
    Task<NotebookStorageSummaryDto?> GetStorageSummaryAsync(string companyId, string notebookId, string userId, bool isAdmin, CancellationToken ct = default);
}

/// <summary>
/// CRUD + execution engine for MotherDuck-style query notebooks. Cross-cell references resolve two ways:
/// same-dataset (the referenced cell's materialized object already lives in the same .duckdb file — SQL
/// just names it) or cross-dataset (this service auto-syncs a fresh, capped copy into the current cell's
/// dataset under the same name before running — see <see cref="SyncReferencedCellAsync"/>). No DuckDB
/// ATTACH is used; everything composes <see cref="IDuckdbService"/>/<see cref="IIngestionService"/>
/// primitives that already exist.
/// </summary>
public class QueryNotebookService : IQueryNotebookService
{
    private static readonly Regex ValidObjectName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private const int CrossDatasetSyncRowCap = 5000;

    private readonly ApplicationDbContext _db;
    private readonly IDuckdbService _duckdb;
    private readonly IDatabaseTableService _dbTables;
    private readonly IIngestionService _ingestion;
    private readonly IDatasetService _datasetService;
    private readonly INotebookSharingService _sharing;
    private readonly INotebookRunCancellationRegistry _cancellation;
    private readonly ILogger<QueryNotebookService> _logger;

    public QueryNotebookService(
        ApplicationDbContext db,
        IDuckdbService duckdb,
        IDatabaseTableService dbTables,
        IIngestionService ingestion,
        IDatasetService datasetService,
        INotebookSharingService sharing,
        INotebookRunCancellationRegistry cancellation,
        ILogger<QueryNotebookService> logger)
    {
        _db = db;
        _duckdb = duckdb;
        _dbTables = dbTables;
        _ingestion = ingestion;
        _datasetService = datasetService;
        _sharing = sharing;
        _cancellation = cancellation;
        _logger = logger;
    }

    // ---- access checks ----

    // IsShared/owner/admin are unchanged from before per-user sharing existed — a per-user grant is purely
    // additive, letting the owner extend view/edit access to specific people on an otherwise-private
    // notebook without flipping the company-wide IsShared flag.
    private async Task<bool> CanViewAsync(QueryNotebook notebook, string userId, bool isAdmin)
    {
        if (isAdmin || notebook.CreatedBy == userId || notebook.IsShared) return true;
        return await _sharing.GetGrantAsync(notebook.Id, userId) != null;
    }

    private async Task<bool> CanEditCellsAsync(QueryNotebook notebook, string userId, bool isAdmin)
    {
        if (isAdmin || notebook.CreatedBy == userId || notebook.IsShared) return true;
        return await _sharing.GetGrantAsync(notebook.Id, userId) == NotebookUserType.Editor;
    }

    // ---- notebook CRUD ----

    public async Task<List<QueryNotebookDto>> GetForCompanyAsync(string companyId, string userId, bool isAdmin)
    {
        var sharedWithMeIds = await _db.NotebookUser
            .Where(nu => nu.UserId == userId)
            .Select(nu => nu.NotebookId)
            .ToListAsync();

        var notebooks = await _db.QueryNotebook
            .Where(n => n.CompanyId == companyId && (n.IsShared || n.CreatedBy == userId || sharedWithMeIds.Contains(n.Id)))
            .OrderByDescending(n => n.ModifiedAt ?? n.CreatedAt)
            .AsNoTracking()
            .ToListAsync();
        if (notebooks.Count == 0) return new();

        var ids = notebooks.Select(n => n.Id).ToList();
        var counts = await _db.QueryNotebookCell
            .Where(c => ids.Contains(c.NotebookId))
            .GroupBy(c => c.NotebookId)
            .Select(g => new { NotebookId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.NotebookId, x => x.Count);
        var grants = await _db.NotebookUser
            .Where(nu => ids.Contains(nu.NotebookId) && nu.UserId == userId)
            .ToDictionaryAsync(nu => nu.NotebookId, nu => nu.Type);

        return notebooks.Select(n => ToDto(n, new(), userId, isAdmin,
            counts.TryGetValue(n.Id, out var cnt) ? cnt : 0,
            grants.TryGetValue(n.Id, out var grant) ? grant : (NotebookUserType?)null)).ToList();
    }

    public async Task<QueryNotebookDto?> GetAsync(string companyId, string id, string userId, bool isAdmin)
    {
        var notebook = await _db.QueryNotebook.AsNoTracking()
            .FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == id);
        if (notebook == null) return null;
        if (!await CanViewAsync(notebook, userId, isAdmin)) return null;

        var cells = await _db.QueryNotebookCell.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.NotebookId == id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        var grant = await _sharing.GetGrantAsync(id, userId);
        return ToDto(notebook, cells, userId, isAdmin, cells.Count, grant);
    }

    public async Task<QueryNotebookDto> CreateAsync(string companyId, string userId, SaveNotebookRequest request)
    {
        var notebook = new QueryNotebook
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Untitled notebook" : request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IsShared = request.IsShared,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = userId,
        };
        _db.QueryNotebook.Add(notebook);
        await _db.SaveChangesAsync();
        return ToDto(notebook, new(), userId, isAdmin: false, cellCount: 0, grant: null);
    }

    public async Task<QueryNotebookDto?> RenameAsync(string companyId, string id, string userId, bool isAdmin, SaveNotebookRequest request)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == id);
        if (notebook == null) return null;
        if (!isAdmin && notebook.CreatedBy != userId) return null;

        notebook.Name = string.IsNullOrWhiteSpace(request.Name) ? notebook.Name : request.Name.Trim();
        notebook.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        notebook.IsShared = request.IsShared;
        notebook.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(companyId, id, userId, isAdmin);
    }

    // Schedule management is owner/admin-only (same bar as rename/delete) — a shared Editor can run cells
    // but shouldn't be able to put a recurring load on someone else's notebook.
    public async Task<QueryNotebookDto?> UpdateScheduleAsync(string companyId, string notebookId, string userId, bool isAdmin, ScheduleNotebookRequest request)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook == null) return null;
        if (!isAdmin && notebook.CreatedBy != userId) return null;

        var cron = string.IsNullOrWhiteSpace(request.CronExpression) ? null : request.CronExpression.Trim();
        if (request.Enabled && cron == null) return null; // can't enable a schedule with no cron expression

        notebook.CronExpression = cron;
        notebook.ScheduleEnabled = request.Enabled && cron != null;
        notebook.ScheduleTimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone.Trim();
        notebook.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(companyId, notebookId, userId, isAdmin);
    }

    public async Task<bool> DeleteAsync(string companyId, string id, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == id);
        if (notebook == null) return false;
        if (!isAdmin && notebook.CreatedBy != userId) return false;

        var cells = await _db.QueryNotebookCell.Where(c => c.CompanyId == companyId && c.NotebookId == id).ToListAsync();
        foreach (var cell in cells)
            await DropMaterializedObjectAsync(cell, ct);

        // No cascade deletes in this project — every FK pointing at query_notebook.id must be cleared
        // explicitly first, or the final Remove below throws a FK violation.
        var shares = await _db.NotebookUser.Where(nu => nu.NotebookId == id).ToListAsync();
        var runHistory = await _db.NotebookCellRun.Where(r => r.NotebookId == id).ToListAsync();

        _db.QueryNotebookCell.RemoveRange(cells);
        _db.NotebookUser.RemoveRange(shares);
        _db.NotebookCellRun.RemoveRange(runHistory);
        _db.QueryNotebook.Remove(notebook);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---- duplicate / export / import ----

    public async Task<QueryNotebookDto?> DuplicateAsync(string companyId, string id, string userId, bool isAdmin, string? newName, CancellationToken ct = default)
    {
        var source = await _db.QueryNotebook.AsNoTracking().FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == id, ct);
        if (source == null) return null;
        if (!await CanViewAsync(source, userId, isAdmin)) return null;

        var sourceCells = await _db.QueryNotebookCell.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.NotebookId == id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

        var clone = new QueryNotebook
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            Name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} (copy)" : newName!.Trim(),
            Description = source.Description,
            IsShared = false, // a duplicate starts private to its new owner regardless of the source's sharing
            CreatedAt = DateTime.UtcNow,
            CreatedBy = userId,
        };
        _db.QueryNotebook.Add(clone);

        // Cell names double as real DuckDB object names and must stay unique per (company, dataset) across
        // every notebook — so a clone can't just reuse the source's names verbatim, they'd collide with the
        // still-existing originals. Rename clashing cells and rewrite any SQL that referenced the old name.
        var idMap = sourceCells.ToDictionary(c => c.Id, _ => Guid.NewGuid().ToString());
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clonedCells = new List<QueryNotebookCell>();

        foreach (var c in sourceCells)
        {
            var newCellName = c.Name;
            if (!string.IsNullOrWhiteSpace(newCellName) && !string.IsNullOrWhiteSpace(c.DatasetId))
            {
                var unique = await EnsureUniqueCellNameAsync(companyId, c.DatasetId, newCellName!, reserved);
                if (!string.Equals(unique, newCellName, StringComparison.OrdinalIgnoreCase)) renames[newCellName!] = unique;
                newCellName = unique;
            }

            clonedCells.Add(new QueryNotebookCell
            {
                Id = idMap[c.Id],
                NotebookId = clone.Id,
                CompanyId = companyId,
                DatasetId = c.DatasetId,
                CellType = c.CellType,
                Name = newCellName,
                SqlText = c.SqlText,
                MarkdownText = c.MarkdownText,
                ReferencedCellIds = JsonSerializer.Serialize(ParseReferencedCellIds(c.ReferencedCellIds).Select(refId => idMap.TryGetValue(refId, out var mapped) ? mapped : refId).ToList()),
                SnapshotMode = c.SnapshotMode,
                SortOrder = c.SortOrder,
                // Not run yet — the clone hasn't materialized its own copy of anything.
                CreatedAt = DateTime.UtcNow,
            });
        }

        if (renames.Count > 0)
            foreach (var cell in clonedCells)
                cell.SqlText = RewriteReferences(cell.SqlText, renames);

        _db.QueryNotebookCell.AddRange(clonedCells);
        await _db.SaveChangesAsync(ct);

        return ToDto(clone, clonedCells, userId, isAdmin: false, cellCount: clonedCells.Count, grant: null);
    }

    public async Task<NotebookExportDto?> ExportAsync(string companyId, string id, string userId, bool isAdmin)
    {
        var notebook = await _db.QueryNotebook.AsNoTracking().FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == id);
        if (notebook == null) return null;
        if (!await CanViewAsync(notebook, userId, isAdmin)) return null;

        var cells = await _db.QueryNotebookCell.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.NotebookId == id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        // Dataset names are exported purely as a human-readable hint for whoever opens the JSON — dataset
        // ids are company-specific and are re-validated (and dropped if they don't resolve) on import.
        var datasetNames = new Dictionary<string, string>();
        foreach (var datasetId in cells.Select(c => c.DatasetId).Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            var ds = await _datasetService.GetDatasetAsync(datasetId!, userId);
            if (ds != null) datasetNames[datasetId!] = ds.Name ?? datasetId!;
        }

        return new NotebookExportDto
        {
            Name = notebook.Name,
            Description = notebook.Description,
            Cells = cells.Select(c => new NotebookExportCellDto
            {
                Id = c.Id,
                CellType = c.CellType,
                Name = c.Name,
                Sql = c.SqlText,
                Markdown = c.MarkdownText,
                ReferencedCellIds = ParseReferencedCellIds(c.ReferencedCellIds),
                SnapshotMode = c.SnapshotMode,
                SortOrder = c.SortOrder,
                DatasetId = c.DatasetId,
                DatasetName = !string.IsNullOrEmpty(c.DatasetId) && datasetNames.TryGetValue(c.DatasetId, out var dn) ? dn : null,
            }).ToList(),
        };
    }

    public async Task<QueryNotebookDto> ImportAsync(string companyId, string userId, NotebookExportDto export)
    {
        var notebook = new QueryNotebook
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            Name = string.IsNullOrWhiteSpace(export.Name) ? "Imported notebook" : export.Name.Trim(),
            Description = export.Description,
            IsShared = false,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = userId,
        };
        _db.QueryNotebook.Add(notebook);

        var idMap = export.Cells.ToDictionary(c => c.Id, _ => Guid.NewGuid().ToString());
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newCells = new List<QueryNotebookCell>();

        foreach (var c in export.Cells)
        {
            // A dataset id only carries over if THIS company can actually access it — importing JSON
            // exported from a different company (or a company where the dataset was since deleted) leaves
            // the cell with no dataset, same as a freshly added cell; the user picks one before running it.
            string? datasetId = null;
            if (!string.IsNullOrWhiteSpace(c.DatasetId))
            {
                var ds = await _datasetService.GetDatasetAsync(c.DatasetId, userId);
                if (ds != null && ds.CompanyId == companyId) datasetId = c.DatasetId;
            }

            var newName = c.Name;
            if (!string.IsNullOrWhiteSpace(newName) && !string.IsNullOrWhiteSpace(datasetId))
            {
                var unique = await EnsureUniqueCellNameAsync(companyId, datasetId, newName!, reserved);
                if (!string.Equals(unique, newName, StringComparison.OrdinalIgnoreCase)) renames[newName!] = unique;
                newName = unique;
            }

            newCells.Add(new QueryNotebookCell
            {
                Id = idMap[c.Id],
                NotebookId = notebook.Id,
                CompanyId = companyId,
                DatasetId = datasetId,
                CellType = c.CellType == "markdown" ? "markdown" : "sql",
                Name = newName,
                SqlText = c.Sql,
                MarkdownText = c.Markdown,
                ReferencedCellIds = JsonSerializer.Serialize(c.ReferencedCellIds.Select(refId => idMap.TryGetValue(refId, out var mapped) ? mapped : refId).ToList()),
                SnapshotMode = c.SnapshotMode,
                SortOrder = c.SortOrder,
                CreatedAt = DateTime.UtcNow,
            });
        }

        if (renames.Count > 0)
            foreach (var cell in newCells)
                cell.SqlText = RewriteReferences(cell.SqlText, renames);

        _db.QueryNotebookCell.AddRange(newCells);
        await _db.SaveChangesAsync();

        return ToDto(notebook, newCells, userId, isAdmin: false, cellCount: newCells.Count, grant: null);
    }

    // Cell names double as DuckDB object names and must stay unique per (company, dataset). Appends
    // "_copy", "_copy2", … until a free name is found — checking both already-persisted cells and names
    // already claimed earlier in the same duplicate/import batch.
    private async Task<string> EnsureUniqueCellNameAsync(string companyId, string datasetId, string baseName, HashSet<string> reservedInThisBatch)
    {
        var candidate = baseName;
        var attempt = 0;
        while (reservedInThisBatch.Contains(candidate) ||
               await _db.QueryNotebookCell.AnyAsync(c => c.CompanyId == companyId && c.DatasetId == datasetId && c.Name == candidate))
        {
            attempt++;
            candidate = attempt == 1 ? $"{baseName}_copy" : $"{baseName}_copy{attempt}";
        }
        reservedInThisBatch.Add(candidate);
        return candidate;
    }

    // Best-effort: rewrites whole-word occurrences of a renamed cell's old name to its new name inside
    // another cloned cell's SQL (e.g. "FROM old_name" -> "FROM new_name"), since cross-cell references are
    // plain identifiers in the SQL text, not something SQL parsing distinguishes from any other token.
    private static string? RewriteReferences(string? sql, Dictionary<string, string> renames)
    {
        if (string.IsNullOrEmpty(sql) || renames.Count == 0) return sql;
        foreach (var (oldName, newName) in renames)
            sql = System.Text.RegularExpressions.Regex.Replace(sql, $@"\b{System.Text.RegularExpressions.Regex.Escape(oldName)}\b", newName, RegexOptions.IgnoreCase);
        return sql;
    }

    // ---- cell CRUD ----

    public async Task<(NotebookCellDto? Cell, string? Error)> AddCellAsync(string companyId, string userId, bool isAdmin, string notebookId, SaveNotebookCellRequest request)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook == null) return (null, "Notebook not found.");
        if (!await CanEditCellsAsync(notebook, userId, isAdmin)) return (null, "You don't have permission to edit this notebook.");

        var validation = await ValidateCellRequestAsync(companyId, request, currentCellId: null);
        if (validation != null) return (null, validation);

        var maxOrder = await _db.QueryNotebookCell
            .Where(c => c.CompanyId == companyId && c.NotebookId == notebookId)
            .Select(c => (int?)c.SortOrder).MaxAsync() ?? -1;

        var cell = new QueryNotebookCell
        {
            Id = Guid.NewGuid().ToString(),
            NotebookId = notebookId,
            CompanyId = companyId,
            DatasetId = request.CellType == "markdown" ? null : request.DatasetId,
            CellType = request.CellType == "markdown" ? "markdown" : "sql",
            Name = request.Name?.Trim(),
            SqlText = request.Sql,
            MarkdownText = request.Markdown,
            ReferencedCellIds = JsonSerializer.Serialize(request.ReferencedCellIds ?? new()),
            SnapshotMode = request.SnapshotMode,
            SortOrder = maxOrder + 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.QueryNotebookCell.Add(cell);
        notebook.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (ToCellDto(cell), null);
    }

    public async Task<(NotebookCellDto? Cell, string? Error)> UpdateCellAsync(string companyId, string userId, bool isAdmin, string notebookId, string cellId, SaveNotebookCellRequest request, CancellationToken ct = default)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook == null) return (null, "Notebook not found.");
        if (!await CanEditCellsAsync(notebook, userId, isAdmin)) return (null, "You don't have permission to edit this notebook.");

        var cell = await _db.QueryNotebookCell
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.NotebookId == notebookId && c.Id == cellId);
        if (cell == null) return (null, "Cell not found.");

        var validation = await ValidateCellRequestAsync(companyId, request, currentCellId: cellId);
        if (validation != null) return (null, validation);

        var newDatasetId = request.CellType == "markdown" ? null : request.DatasetId;
        var newName = request.Name?.Trim();

        // If the cell's materialized identity (name or dataset) is changing, drop the old object first —
        // otherwise it's an orphaned table nobody references anymore.
        if (!string.IsNullOrEmpty(cell.LastMaterializedObject) && (cell.DatasetId != newDatasetId || cell.Name != newName))
        {
            await DropMaterializedObjectAsync(cell, ct);
            cell.LastMaterializedObject = null;
            cell.LastRunStatus = null;
            cell.LastRunError = null;
        }

        cell.DatasetId = newDatasetId;
        cell.CellType = request.CellType == "markdown" ? "markdown" : "sql";
        cell.Name = newName;
        cell.SqlText = request.Sql;
        cell.MarkdownText = request.Markdown;
        cell.ReferencedCellIds = JsonSerializer.Serialize(request.ReferencedCellIds ?? new());
        cell.SnapshotMode = request.SnapshotMode;
        cell.ModifiedAt = DateTime.UtcNow;

        await TouchNotebookAsync(companyId, notebookId);
        await _db.SaveChangesAsync();
        return (ToCellDto(cell), null);
    }

    public async Task<bool> RemoveCellAsync(string companyId, string userId, bool isAdmin, string notebookId, string cellId, CancellationToken ct = default)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook == null || !await CanEditCellsAsync(notebook, userId, isAdmin)) return false;

        var cell = await _db.QueryNotebookCell
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.NotebookId == notebookId && c.Id == cellId);
        if (cell == null) return false;

        await DropMaterializedObjectAsync(cell, ct);
        _db.QueryNotebookCell.Remove(cell);
        await TouchNotebookAsync(companyId, notebookId);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReorderCellsAsync(string companyId, string userId, bool isAdmin, string notebookId, List<string> orderedCellIds)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook == null || !await CanEditCellsAsync(notebook, userId, isAdmin)) return false;

        var cells = await _db.QueryNotebookCell
            .Where(c => c.CompanyId == companyId && c.NotebookId == notebookId)
            .ToListAsync();
        if (cells.Count == 0) return false;

        for (var i = 0; i < orderedCellIds.Count; i++)
        {
            var cell = cells.FirstOrDefault(c => c.Id == orderedCellIds[i]);
            if (cell != null) cell.SortOrder = i;
        }
        await TouchNotebookAsync(companyId, notebookId);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---- validation ----

    private async Task<string?> ValidateCellRequestAsync(string companyId, SaveNotebookCellRequest request, string? currentCellId)
    {
        if (request.CellType == "markdown") return null;
        // A cell can be created/saved as an empty shell (no dataset/name yet) — Name and DatasetId are
        // only hard-required at RunCellAsync time. Here we only validate content that IS present.
        if (string.IsNullOrWhiteSpace(request.Name)) return null;

        var name = request.Name.Trim();
        if (!ValidObjectName.IsMatch(name))
            return "Cell name must start with a letter or underscore and contain only letters, digits and underscores.";

        if (!string.IsNullOrWhiteSpace(request.DatasetId))
        {
            // Names double as real DuckDB object names — they must be unique per dataset across every
            // notebook (not just this one), or two cells would silently overwrite each other's table.
            var clash = await _db.QueryNotebookCell.AnyAsync(c =>
                c.CompanyId == companyId && c.DatasetId == request.DatasetId && c.Name == name &&
                (currentCellId == null || c.Id != currentCellId));
            if (clash)
                return $"Another cell already uses the name '{name}' in this dataset — pick a different name.";
        }

        return null;
    }

    // ---- execution ----

    public async Task<NotebookCellRunResult> RunCellAsync(string companyId, string userId, bool isAdmin, string notebookId, string cellId, Dictionary<string, string>? parameters = null, string triggeredBy = "manual", CancellationToken ct = default)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId, ct);
        if (notebook == null) return new NotebookCellRunResult { Error = "Notebook not found." };
        if (!await CanEditCellsAsync(notebook, userId, isAdmin)) return new NotebookCellRunResult { Error = "You don't have permission to run cells in this notebook." };

        var cell = await _db.QueryNotebookCell
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.NotebookId == notebookId && c.Id == cellId, ct);
        if (cell == null) return new NotebookCellRunResult { Error = "Cell not found." };
        if (cell.CellType != "sql") return new NotebookCellRunResult { Error = "Only SQL cells can be run." };
        if (string.IsNullOrWhiteSpace(cell.DatasetId)) return new NotebookCellRunResult { Error = "This cell has no dataset selected." };
        if (string.IsNullOrWhiteSpace(cell.Name)) return new NotebookCellRunResult { Error = "This cell needs a name before it can run." };

        var cancelKey = $"cell:{cellId}";
        var runCt = _cancellation.Begin(cancelKey, ct);
        var startedAt = DateTime.UtcNow;
        try
        {
            var dataset = await _datasetService.GetDatasetAsync(cell.DatasetId, userId);
            if (dataset == null) return await FailAsync(cell, "You don't have access to this cell's dataset.", startedAt, triggeredBy, ct);

            // Resolve dependencies: fail fast if a referenced cell has never produced a result, and sync a
            // fresh copy in for any that live in a different dataset.
            foreach (var refId in ParseReferencedCellIds(cell.ReferencedCellIds))
            {
                var referenced = await _db.QueryNotebookCell.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == refId, runCt);
                if (referenced == null) continue;
                if (string.IsNullOrEmpty(referenced.LastMaterializedObject) || string.IsNullOrEmpty(referenced.DatasetId))
                    return await FailAsync(cell, $"Cell '{referenced.Name}' hasn't produced a result yet — run it first.", startedAt, triggeredBy, ct);

                if (await _datasetService.GetDatasetAsync(referenced.DatasetId, userId) == null)
                    return await FailAsync(cell, $"You don't have access to the dataset cell '{referenced.Name}' depends on.", startedAt, triggeredBy, ct);

                if (!string.Equals(referenced.DatasetId, cell.DatasetId, StringComparison.Ordinal))
                {
                    var syncError = await SyncReferencedCellAsync(referenced, cell.DatasetId!, runCt);
                    if (syncError != null) return await FailAsync(cell, syncError, startedAt, triggeredBy, ct);
                }
            }

            // Parameters are substituted into a COPY of the SQL for this run only — the stored cell text
            // (and therefore what later edits/diffs see) never changes.
            var sqlToRun = SubstituteParameters(cell.SqlText, parameters);

            // Classify + materialize (single SELECT only), then read back a cheap preview from the materialized
            // object so the expensive query only runs once.
            var isSingleSelect = IsSingleSelect(sqlToRun);
            NotebookCellRunResult result;

            if (isSingleSelect)
            {
                SqlQueryResult materialize;
                if (dataset.SourceType == DatasetSourceType.External && !cell.SnapshotMode)
                {
                    var import = await _ingestion.SnapshotQueryAsync(companyId, cell.DatasetId!, dataset.SourceEntityId ?? "", sqlToRun, cell.Name!, runCt);
                    materialize = new SqlQueryResult { Error = import.Error, RowsAffected = import.RowsInserted + import.RowsUpdated };
                }
                else
                {
                    materialize = await _duckdb.CreateObjectFromQueryAsync(cell.DatasetId!, cell.Name!, sqlToRun, asView: false, runCt);
                }

                if (materialize.Error != null)
                    return await FailAsync(cell, materialize.Error, startedAt, triggeredBy, ct);

                var preview = await _duckdb.ExecuteSqlAsync(cell.DatasetId!, $"SELECT * FROM \"{cell.Name}\" LIMIT 5000", allowWrite: false, maxRows: 5000, runCt);
                result = new NotebookCellRunResult
                {
                    Columns = preview.Columns,
                    Rows = preview.Rows,
                    RowsReturned = preview.RowsReturned,
                    Truncated = preview.Truncated,
                    ElapsedMs = materialize.ElapsedMs + preview.ElapsedMs,
                    IsSelect = true,
                    Error = preview.Error,
                    MaterializedObjectName = cell.Name,
                    ReferenceToken = cell.Name,
                };
            }
            else
            {
                var run = await _duckdb.ExecuteSqlAsync(cell.DatasetId!, sqlToRun, allowWrite: true, maxRows: 5000, runCt);
                result = new NotebookCellRunResult
                {
                    Columns = run.Columns,
                    Rows = run.Rows,
                    RowsReturned = run.RowsReturned,
                    Truncated = run.Truncated,
                    ElapsedMs = run.ElapsedMs,
                    RowsAffected = run.RowsAffected,
                    IsSelect = run.IsSelect,
                    Error = run.Error,
                };
            }

            cell.LastRunStatus = result.Error == null ? "success" : "error";
            cell.LastRunError = result.Error;
            cell.LastMaterializedObject = result.Error == null ? result.MaterializedObjectName : cell.LastMaterializedObject;
            cell.ModifiedAt = DateTime.UtcNow;
            LogRun(notebookId, cellId, companyId, cell.LastRunStatus, result.Error, result.RowsReturned, result.ElapsedMs, cell.LastMaterializedObject, triggeredBy, startedAt);
            await _db.SaveChangesAsync(ct);

            return result;
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Cancelled via the registry (the user's explicit "Cancel" click), not the caller's own token
            // (e.g. an HTTP disconnect) — record it as a deliberate stop rather than letting it look like a
            // crash. Uses the ORIGINAL ct for this bookkeeping save since runCt is the one that's cancelled.
            return await FailAsync(cell, "Run cancelled.", startedAt, triggeredBy, ct);
        }
        finally
        {
            _cancellation.End(cancelKey);
        }
    }

    public async Task<RunAllResult> RunAllAsync(string companyId, string userId, bool isAdmin, string notebookId, Dictionary<string, string>? parameters = null, string triggeredBy = "run_all", CancellationToken ct = default)
    {
        var result = new RunAllResult();

        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId, ct);
        if (notebook == null || !await CanEditCellsAsync(notebook, userId, isAdmin))
        {
            result.Cells.Add(new CellRunSummary { CellId = "", Status = "error", Error = "You don't have permission to run this notebook." });
            return result;
        }

        var cancelKey = $"notebook:{notebookId}:all";
        var runCt = _cancellation.Begin(cancelKey, ct);
        try
        {
            var cells = await _db.QueryNotebookCell
                .Where(c => c.CompanyId == companyId && c.NotebookId == notebookId)
                .OrderBy(c => c.SortOrder)
                .ToListAsync(runCt);

            var sqlCells = cells.Where(c => c.CellType == "sql").ToList();
            var (order, cycleMembers) = TopologicalOrder(sqlCells);

            if (cycleMembers.Count > 0)
            {
                foreach (var c in cycleMembers)
                    result.Cells.Add(new CellRunSummary { CellId = c.Id, Status = "error", Error = "Circular reference among notebook cells — break the cycle and try again." });
                return result;
            }

            var failed = false;
            foreach (var cell in order)
            {
                if (failed)
                {
                    result.Cells.Add(new CellRunSummary { CellId = cell.Id, Status = "skipped" });
                    continue;
                }
                if (runCt.IsCancellationRequested)
                {
                    result.Cells.Add(new CellRunSummary { CellId = cell.Id, Status = "skipped", Error = "Run cancelled." });
                    continue;
                }

                var runResult = await RunCellAsync(companyId, userId, isAdmin, notebookId, cell.Id, parameters, triggeredBy, runCt);
                if (runResult.Error != null)
                {
                    result.Cells.Add(new CellRunSummary { CellId = cell.Id, Status = "error", Error = runResult.Error, Result = runResult });
                    failed = true;
                }
                else
                {
                    result.Cells.Add(new CellRunSummary { CellId = cell.Id, Status = "success", Result = runResult });
                }
            }

            if (triggeredBy == "scheduled")
            {
                var anyError = result.Cells.Any(c => c.Status == "error");
                notebook.LastScheduledRunAt = DateTime.UtcNow;
                notebook.LastScheduledRunStatus = anyError ? "error" : "success";
                notebook.LastScheduledRunError = anyError
                    ? string.Join("; ", result.Cells.Where(c => c.Status == "error").Select(c => c.Error))
                    : null;
                // Always persist the schedule's own status, even if the run itself was cancelled mid-way.
                await _db.SaveChangesAsync(CancellationToken.None);
            }

            return result;
        }
        finally
        {
            _cancellation.End(cancelKey);
        }
    }

    public async Task<List<NotebookCellRunDto>> GetCellRunHistoryAsync(string companyId, string notebookId, string cellId, string userId, bool isAdmin, int take = 20)
    {
        var notebook = await _db.QueryNotebook.AsNoTracking().FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook == null || !await CanViewAsync(notebook, userId, isAdmin)) return new();

        var runs = await _db.NotebookCellRun.AsNoTracking()
            .Where(r => r.NotebookId == notebookId && r.CellId == cellId)
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();

        return runs.Select(r => new NotebookCellRunDto
        {
            Id = r.Id,
            Status = r.Status,
            Error = r.Error,
            RowsReturned = r.RowsReturned,
            ElapsedMs = r.ElapsedMs,
            MaterializedObject = r.MaterializedObject,
            TriggeredBy = r.TriggeredBy,
            StartedAt = r.StartedAt,
        }).ToList();
    }

    // A notebook's cells can span multiple datasets, and a dataset's .duckdb file can hold tables from
    // other sources too — so this can't just sum GetDatasetTableSummaryAsync (dataset-wide) per dataset.
    // Instead it groups cells by dataset, pulls that dataset's per-table stats once, and keeps only the
    // rows matching one of THIS notebook's own materialized cell names.
    public async Task<NotebookStorageSummaryDto?> GetStorageSummaryAsync(string companyId, string notebookId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var notebook = await _db.QueryNotebook.AsNoTracking().FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId, ct);
        if (notebook == null || !await CanViewAsync(notebook, userId, isAdmin)) return null;

        var cells = await _db.QueryNotebookCell.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.NotebookId == notebookId
                && c.DatasetId != null && c.LastMaterializedObject != null)
            .ToListAsync(ct);

        var summary = new NotebookStorageSummaryDto();
        foreach (var group in cells.GroupBy(c => c.DatasetId!))
        {
            var objectNames = group.Select(c => c.LastMaterializedObject!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<TableStats> stats;
            try { stats = await _duckdb.GetTableStatsAsync(group.Key, ct); }
            catch { continue; } // dataset's .duckdb unreadable/locked — skip rather than fail the whole summary

            foreach (var s in stats.Where(s => objectNames.Contains(s.TableName)))
            {
                summary.ObjectCount++;
                summary.TotalRows += s.RowCount;
                summary.TotalSizeBytes += s.SizeBytes;
            }
        }

        return summary;
    }

    // ---- cross-dataset sync ----

    // Copies up to CrossDatasetSyncRowCap rows from a referenced cell's materialized object (in its own
    // dataset) into the target dataset under the SAME name, so the target cell's SQL can just say
    // `FROM {referenced.Name}` regardless of which dataset it originally came from. No ATTACH — a plain
    // read + CSV round-trip via existing primitives. Returns an error message, or null on success.
    private async Task<string?> SyncReferencedCellAsync(QueryNotebookCell referenced, string targetDatasetId, CancellationToken ct)
    {
        var read = await _duckdb.ExecuteSqlAsync(referenced.DatasetId!, $"SELECT * FROM \"{referenced.LastMaterializedObject}\" LIMIT {CrossDatasetSyncRowCap}", allowWrite: false, maxRows: CrossDatasetSyncRowCap, ct);
        if (read.Error != null) return $"Couldn't read cell '{referenced.Name}' for cross-dataset sync: {read.Error}";

        await using var csv = BuildCsv(read);
        var import = await _duckdb.ImportFileAsync(targetDatasetId, referenced.Name!, csv, ImportFileFormat.Csv, ImportMode.Replace, new List<string>(), skipInvalidRows: true, createIfMissing: true, ct);
        return import.Error;
    }

    private static MemoryStream BuildCsv(SqlQueryResult result)
    {
        var sb = new StringBuilder();
        var columnNames = result.Columns.Count > 0 ? result.Columns.Select(c => c.Name).ToList() : result.Rows.FirstOrDefault()?.Keys.ToList() ?? new();
        sb.AppendLine(string.Join(',', columnNames.Select(CsvEscape)));
        foreach (var row in result.Rows)
        {
            var cells = columnNames.Select(c => row.TryGetValue(c, out var v) ? FormatCsvValue(v) : string.Empty);
            sb.AppendLine(string.Join(',', cells.Select(CsvEscape)));
        }
        return new MemoryStream(new UTF8Encoding(false).GetBytes(sb.ToString()));
    }

    private static string FormatCsvValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string CsvEscape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // ---- cleanup ----

    private async Task DropMaterializedObjectAsync(QueryNotebookCell cell, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cell.LastMaterializedObject) || string.IsNullOrEmpty(cell.DatasetId)) return;
        try
        {
            // Either could have been created as a table or a view — drop both, IF EXISTS makes it safe.
            await _duckdb.ExecuteSqlAsync(cell.DatasetId, $"DROP VIEW IF EXISTS \"{cell.LastMaterializedObject}\"", allowWrite: true, maxRows: 0, ct);
            await _duckdb.ExecuteSqlAsync(cell.DatasetId, $"DROP TABLE IF EXISTS \"{cell.LastMaterializedObject}\"", allowWrite: true, maxRows: 0, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Notebook] Failed to drop materialized object {Object} in dataset {Dataset}.", cell.LastMaterializedObject, cell.DatasetId);
        }
    }

    private async Task<NotebookCellRunResult> FailAsync(QueryNotebookCell cell, string error, DateTime startedAt, string triggeredBy, CancellationToken ct)
    {
        cell.LastRunStatus = "error";
        cell.LastRunError = error;
        cell.ModifiedAt = DateTime.UtcNow;
        LogRun(cell.NotebookId, cell.Id, cell.CompanyId, "error", error, null, null, null, triggeredBy, startedAt);
        await _db.SaveChangesAsync(ct);
        return new NotebookCellRunResult { Error = error };
    }

    // Queues a run-history row on the change tracker — the caller's own SaveChangesAsync (right after,
    // alongside the cell's own LastRunStatus update) persists it, so one cell run = one atomic save.
    private void LogRun(string notebookId, string cellId, string companyId, string status, string? error, int? rowsReturned, long? elapsedMs, string? materializedObject, string triggeredBy, DateTime startedAt)
    {
        _db.NotebookCellRun.Add(new NotebookCellRun
        {
            Id = Guid.NewGuid().ToString(),
            NotebookId = notebookId,
            CellId = cellId,
            CompanyId = companyId,
            Status = status,
            Error = error,
            RowsReturned = rowsReturned,
            ElapsedMs = elapsedMs,
            MaterializedObject = materializedObject,
            TriggeredBy = triggeredBy,
            StartedAt = startedAt,
        });
    }

    // {{name}} placeholders are substituted into a COPY of the SQL used only for this run — unmatched
    // placeholders are left as-is so a missing parameter fails loudly at the SQL parser rather than
    // silently becoming an empty string.
    private static string SubstituteParameters(string? sql, Dictionary<string, string>? parameters)
    {
        if (string.IsNullOrEmpty(sql) || parameters == null || parameters.Count == 0) return sql ?? "";
        return Regex.Replace(sql, @"\{\{\s*(\w+)\s*\}\}", m => parameters.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private async Task TouchNotebookAsync(string companyId, string notebookId)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook != null) notebook.ModifiedAt = DateTime.UtcNow;
    }

    // ---- ordering ----

    // Kahn's algorithm. Returns (order, cycleMembers) — cycleMembers is non-empty only if a cycle exists,
    // in which case order is meaningless and the caller should report the cycle instead of running.
    private static (List<QueryNotebookCell> Order, List<QueryNotebookCell> CycleMembers) TopologicalOrder(List<QueryNotebookCell> cells)
    {
        var byId = cells.ToDictionary(c => c.Id);
        var inDegree = cells.ToDictionary(c => c.Id, _ => 0);
        var dependents = cells.ToDictionary(c => c.Id, _ => new List<string>());

        foreach (var cell in cells)
        {
            foreach (var refId in ParseReferencedCellIds(cell.ReferencedCellIds))
            {
                if (!byId.ContainsKey(refId)) continue; // reference outside this cell set (e.g. a markdown id) — ignore
                inDegree[cell.Id]++;
                dependents[refId].Add(cell.Id);
            }
        }

        var queue = new Queue<QueryNotebookCell>(cells.Where(c => inDegree[c.Id] == 0).OrderBy(c => c.SortOrder));
        var order = new List<QueryNotebookCell>();

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            order.Add(cell);
            foreach (var depId in dependents[cell.Id])
            {
                if (--inDegree[depId] == 0) queue.Enqueue(byId[depId]);
            }
        }

        if (order.Count == cells.Count) return (order, new());
        var cycleMembers = cells.Where(c => !order.Contains(c)).ToList();
        return (new(), cycleMembers);
    }

    private static List<string> ParseReferencedCellIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static bool IsSingleSelect(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;
        var statements = sql.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (statements.Count != 1) return false;
        var firstWord = new string(statements[0].TrimStart().TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return firstWord is "SELECT" or "WITH";
    }

    // ---- DTO mapping ----

    private static QueryNotebookDto ToDto(QueryNotebook n, List<QueryNotebookCell> cells, string userId, bool isAdmin, int cellCount, NotebookUserType? grant) => new()
    {
        Id = n.Id,
        Name = n.Name,
        Description = n.Description,
        IsShared = n.IsShared,
        CreatedBy = n.CreatedBy,
        CreatedAt = n.CreatedAt,
        ModifiedAt = n.ModifiedAt,
        CanEdit = isAdmin || n.CreatedBy == userId,
        CanEditCells = isAdmin || n.CreatedBy == userId || n.IsShared || grant == NotebookUserType.Editor,
        CellCount = cellCount,
        Cells = cells.Select(ToCellDto).ToList(),
        CronExpression = n.CronExpression,
        ScheduleEnabled = n.ScheduleEnabled,
        ScheduleTimeZone = n.ScheduleTimeZone,
        LastScheduledRunAt = n.LastScheduledRunAt,
        LastScheduledRunStatus = n.LastScheduledRunStatus,
        LastScheduledRunError = n.LastScheduledRunError,
    };

    private static NotebookCellDto ToCellDto(QueryNotebookCell c) => new()
    {
        Id = c.Id,
        DatasetId = c.DatasetId,
        CellType = c.CellType,
        Name = c.Name,
        Sql = c.SqlText,
        Markdown = c.MarkdownText,
        ReferencedCellIds = ParseReferencedCellIds(c.ReferencedCellIds),
        SnapshotMode = c.SnapshotMode,
        SortOrder = c.SortOrder,
        LastRunStatus = c.LastRunStatus,
        LastRunError = c.LastRunError,
        LastMaterializedObject = c.LastMaterializedObject,
    };
}

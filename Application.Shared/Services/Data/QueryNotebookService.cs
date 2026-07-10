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

    Task<(NotebookCellDto? Cell, string? Error)> AddCellAsync(string companyId, string notebookId, SaveNotebookCellRequest request);
    Task<(NotebookCellDto? Cell, string? Error)> UpdateCellAsync(string companyId, string notebookId, string cellId, SaveNotebookCellRequest request, CancellationToken ct = default);
    Task<bool> RemoveCellAsync(string companyId, string notebookId, string cellId, CancellationToken ct = default);
    Task<bool> ReorderCellsAsync(string companyId, string notebookId, List<string> orderedCellIds);

    Task<NotebookCellRunResult> RunCellAsync(string companyId, string userId, string notebookId, string cellId, CancellationToken ct = default);
    Task<RunAllResult> RunAllAsync(string companyId, string userId, string notebookId, CancellationToken ct = default);
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
    private readonly ILogger<QueryNotebookService> _logger;

    public QueryNotebookService(
        ApplicationDbContext db,
        IDuckdbService duckdb,
        IDatabaseTableService dbTables,
        IIngestionService ingestion,
        IDatasetService datasetService,
        ILogger<QueryNotebookService> logger)
    {
        _db = db;
        _duckdb = duckdb;
        _dbTables = dbTables;
        _ingestion = ingestion;
        _datasetService = datasetService;
        _logger = logger;
    }

    // ---- notebook CRUD ----

    public async Task<List<QueryNotebookDto>> GetForCompanyAsync(string companyId, string userId, bool isAdmin)
    {
        var notebooks = await _db.QueryNotebook
            .Where(n => n.CompanyId == companyId && (n.IsShared || n.CreatedBy == userId))
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

        return notebooks.Select(n => ToDto(n, new(), userId, isAdmin, counts.TryGetValue(n.Id, out var cnt) ? cnt : 0)).ToList();
    }

    public async Task<QueryNotebookDto?> GetAsync(string companyId, string id, string userId, bool isAdmin)
    {
        var notebook = await _db.QueryNotebook.AsNoTracking()
            .FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == id);
        if (notebook == null) return null;
        if (!notebook.IsShared && notebook.CreatedBy != userId && !isAdmin) return null;

        var cells = await _db.QueryNotebookCell.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.NotebookId == id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        return ToDto(notebook, cells, userId, isAdmin, cells.Count);
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
        return ToDto(notebook, new(), userId, isAdmin: false, cellCount: 0);
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

    public async Task<bool> DeleteAsync(string companyId, string id, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == id);
        if (notebook == null) return false;
        if (!isAdmin && notebook.CreatedBy != userId) return false;

        var cells = await _db.QueryNotebookCell.Where(c => c.CompanyId == companyId && c.NotebookId == id).ToListAsync();
        foreach (var cell in cells)
            await DropMaterializedObjectAsync(cell, ct);

        _db.QueryNotebookCell.RemoveRange(cells);
        _db.QueryNotebook.Remove(notebook);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---- cell CRUD ----

    public async Task<(NotebookCellDto? Cell, string? Error)> AddCellAsync(string companyId, string notebookId, SaveNotebookCellRequest request)
    {
        var notebook = await _db.QueryNotebook.FirstOrDefaultAsync(n => n.CompanyId == companyId && n.Id == notebookId);
        if (notebook == null) return (null, "Notebook not found.");

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

    public async Task<(NotebookCellDto? Cell, string? Error)> UpdateCellAsync(string companyId, string notebookId, string cellId, SaveNotebookCellRequest request, CancellationToken ct = default)
    {
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

    public async Task<bool> RemoveCellAsync(string companyId, string notebookId, string cellId, CancellationToken ct = default)
    {
        var cell = await _db.QueryNotebookCell
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.NotebookId == notebookId && c.Id == cellId);
        if (cell == null) return false;

        await DropMaterializedObjectAsync(cell, ct);
        _db.QueryNotebookCell.Remove(cell);
        await TouchNotebookAsync(companyId, notebookId);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReorderCellsAsync(string companyId, string notebookId, List<string> orderedCellIds)
    {
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

    public async Task<NotebookCellRunResult> RunCellAsync(string companyId, string userId, string notebookId, string cellId, CancellationToken ct = default)
    {
        var cell = await _db.QueryNotebookCell
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.NotebookId == notebookId && c.Id == cellId, ct);
        if (cell == null) return new NotebookCellRunResult { Error = "Cell not found." };
        if (cell.CellType != "sql") return new NotebookCellRunResult { Error = "Only SQL cells can be run." };
        if (string.IsNullOrWhiteSpace(cell.DatasetId)) return new NotebookCellRunResult { Error = "This cell has no dataset selected." };
        if (string.IsNullOrWhiteSpace(cell.Name)) return new NotebookCellRunResult { Error = "This cell needs a name before it can run." };

        var dataset = await _datasetService.GetDatasetAsync(cell.DatasetId, userId);
        if (dataset == null) return await FailAsync(cell, "You don't have access to this cell's dataset.", ct);

        // Resolve dependencies: fail fast if a referenced cell has never produced a result, and sync a
        // fresh copy in for any that live in a different dataset.
        foreach (var refId in ParseReferencedCellIds(cell.ReferencedCellIds))
        {
            var referenced = await _db.QueryNotebookCell.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == refId, ct);
            if (referenced == null) continue;
            if (string.IsNullOrEmpty(referenced.LastMaterializedObject) || string.IsNullOrEmpty(referenced.DatasetId))
                return await FailAsync(cell, $"Cell '{referenced.Name}' hasn't produced a result yet — run it first.", ct);

            if (await _datasetService.GetDatasetAsync(referenced.DatasetId, userId) == null)
                return await FailAsync(cell, $"You don't have access to the dataset cell '{referenced.Name}' depends on.", ct);

            if (!string.Equals(referenced.DatasetId, cell.DatasetId, StringComparison.Ordinal))
            {
                var syncError = await SyncReferencedCellAsync(referenced, cell.DatasetId!, ct);
                if (syncError != null) return await FailAsync(cell, syncError, ct);
            }
        }

        // Classify + materialize (single SELECT only), then read back a cheap preview from the materialized
        // object so the expensive query only runs once.
        var isSingleSelect = IsSingleSelect(cell.SqlText);
        NotebookCellRunResult result;

        if (isSingleSelect)
        {
            SqlQueryResult materialize;
            if (dataset.SourceType == DatasetSourceType.External && !cell.SnapshotMode)
            {
                var import = await _ingestion.SnapshotQueryAsync(companyId, cell.DatasetId!, dataset.SourceEntityId ?? "", cell.SqlText ?? "", cell.Name!, ct);
                materialize = new SqlQueryResult { Error = import.Error, RowsAffected = import.RowsInserted + import.RowsUpdated };
            }
            else
            {
                materialize = await _duckdb.CreateObjectFromQueryAsync(cell.DatasetId!, cell.Name!, cell.SqlText ?? "", asView: false, ct);
            }

            if (materialize.Error != null)
                return await FailAsync(cell, materialize.Error, ct);

            var preview = await _duckdb.ExecuteSqlAsync(cell.DatasetId!, $"SELECT * FROM \"{cell.Name}\" LIMIT 5000", allowWrite: false, maxRows: 5000, ct);
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
            var run = await _duckdb.ExecuteSqlAsync(cell.DatasetId!, cell.SqlText ?? "", allowWrite: true, maxRows: 5000, ct);
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
        await _db.SaveChangesAsync(ct);

        return result;
    }

    public async Task<RunAllResult> RunAllAsync(string companyId, string userId, string notebookId, CancellationToken ct = default)
    {
        var result = new RunAllResult();
        var cells = await _db.QueryNotebookCell
            .Where(c => c.CompanyId == companyId && c.NotebookId == notebookId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

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

            var runResult = await RunCellAsync(companyId, userId, notebookId, cell.Id, ct);
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

        return result;
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

    private async Task<NotebookCellRunResult> FailAsync(QueryNotebookCell cell, string error, CancellationToken ct)
    {
        cell.LastRunStatus = "error";
        cell.LastRunError = error;
        cell.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new NotebookCellRunResult { Error = error };
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

    private static QueryNotebookDto ToDto(QueryNotebook n, List<QueryNotebookCell> cells, string userId, bool isAdmin, int cellCount) => new()
    {
        Id = n.Id,
        Name = n.Name,
        Description = n.Description,
        IsShared = n.IsShared,
        CreatedBy = n.CreatedBy,
        CreatedAt = n.CreatedAt,
        ModifiedAt = n.ModifiedAt,
        CanEdit = isAdmin || n.CreatedBy == userId,
        CellCount = cellCount,
        Cells = cells.Select(ToCellDto).ToList(),
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

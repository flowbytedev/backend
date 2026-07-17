using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Services;
using Application.Shared.Services.Logging;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

public interface IDatasetDocService
{
    /// <summary>
    /// Every live column of the table merged with its saved documentation (left-join on column name),
    /// so undocumented columns still appear with null doc fields. When <paramref name="snapshotMode"/> is
    /// true the columns are read from the dataset's DuckDB snapshot; when false they are read from the
    /// External dataset's live source table.
    /// </summary>
    Task<TableDocDto> GetTableDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default);

    /// <summary>Upserts user edits to column docs (manual — clears the AI-generated flag).</summary>
    Task<TableDocDto> SaveColumnDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, string? userId, List<SaveColumnDocRequest> edits, CancellationToken ct = default);

    /// <summary>Upserts AI-generated column docs (sets is_ai_generated + generated_at). Used by the generator.</summary>
    Task ApplyGeneratedDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, List<SaveColumnDocRequest> generated, CancellationToken ct = default);

    /// <summary>
    /// The live columns (name + structural type) of a table, read from the dataset's DuckDB snapshot
    /// (<paramref name="snapshotMode"/> true) or from the External dataset's live source (false). Returns
    /// an empty list when the source can't be reached or the dataset isn't external in source mode.
    /// </summary>
    Task<List<Column>> GetLiveColumnsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default);

    /// <summary>
    /// The saved documentation overlay for a table (no live columns), keyed by column name
    /// (case-insensitive). Lets callers enrich a schema they already have without re-reading columns.
    /// </summary>
    Task<Dictionary<string, ColumnDocDto>> GetSavedColumnDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default);

    /// <summary>
    /// Names of the dataset's tables that already have at least one saved column doc in the given layer
    /// (<paramref name="snapshotMode"/> true = DuckDB snapshot; false = live source). Lets table lists
    /// badge which tables are documented without loading each table's docs.
    /// </summary>
    Task<List<string>> GetDocumentedTablesAsync(string companyId, string datasetId, bool snapshotMode, CancellationToken ct = default);
}

/// <summary>
/// CRUD for the semantic layer — per-column documentation of dataset tables. Company- (and dataset/
/// table-) scoped. Column structure is read live: from DuckDB (<see cref="IDuckdbService"/>) for the
/// snapshot layer, or from the External dataset's source database (<see cref="IDatabaseTableService"/>)
/// for the source layer. This service only manages the documentation overlay persisted in
/// <c>dataset_column_doc</c>, keyed by (company, dataset, table, column, is_snapshot).
/// </summary>
public class DatasetDocService : IDatasetDocService
{
    private readonly ApplicationDbContext _db;
    private readonly IDuckdbService _duckdb;
    private readonly IDatabaseTableService _dbTables;
    private readonly IDebugLogService _debug;

    public DatasetDocService(ApplicationDbContext db, IDuckdbService duckdb, IDatabaseTableService dbTables, IDebugLogService debug)
    {
        _db = db;
        _duckdb = duckdb;
        _dbTables = dbTables;
        _debug = debug;
    }

    public async Task<TableDocDto> GetTableDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default)
    {
        var columns = await GetLiveColumnsAsync(companyId, datasetId, tableName, snapshotMode, ct);
        var byName = await GetSavedColumnDocsAsync(companyId, datasetId, tableName, snapshotMode, ct);

        var result = new TableDocDto { DatasetId = datasetId, TableName = tableName };
        foreach (var c in columns)
        {
            byName.TryGetValue(c.Name, out var doc);
            result.Columns.Add(new ColumnDocDto
            {
                ColumnName = c.Name,
                DataType = c.DataType ?? string.Empty,
                DisplayName = doc?.DisplayName,
                Description = doc?.Description,
                SemanticType = doc?.SemanticType,
                Unit = doc?.Unit,
                IsPii = doc?.IsPii ?? false,
                PiiType = doc?.PiiType,
                IsAiGenerated = doc?.IsAiGenerated ?? false,
            });
        }
        return result;
    }

    public async Task<Dictionary<string, ColumnDocDto>> GetSavedColumnDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default)
    {
        var docs = await _db.DatasetColumnDoc.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.DatasetId == datasetId && d.TableName == tableName && d.IsSnapshot == snapshotMode)
            .ToListAsync(ct);

        return docs.ToDictionary(
            d => d.ColumnName,
            d => new ColumnDocDto
            {
                ColumnName = d.ColumnName,
                DisplayName = d.DisplayName,
                Description = d.Description,
                SemanticType = d.SemanticType,
                Unit = d.Unit,
                IsPii = d.IsPii,
                PiiType = d.PiiType,
                IsAiGenerated = d.IsAiGenerated,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<string>> GetDocumentedTablesAsync(string companyId, string datasetId, bool snapshotMode, CancellationToken ct = default)
        => await _db.DatasetColumnDoc.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.DatasetId == datasetId && d.IsSnapshot == snapshotMode)
            .Select(d => d.TableName)
            .Distinct()
            .ToListAsync(ct);

    public async Task<TableDocDto> SaveColumnDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, string? userId, List<SaveColumnDocRequest> edits, CancellationToken ct = default)
    {
        await UpsertAsync(companyId, datasetId, tableName, snapshotMode, edits, aiGenerated: false, userId, ct);
        return await GetTableDocsAsync(companyId, datasetId, tableName, snapshotMode, ct);
    }

    public Task ApplyGeneratedDocsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, List<SaveColumnDocRequest> generated, CancellationToken ct = default)
        => UpsertAsync(companyId, datasetId, tableName, snapshotMode, generated, aiGenerated: true, userId: null, ct);

    public async Task<List<Column>> GetLiveColumnsAsync(string companyId, string datasetId, string tableName, bool snapshotMode, CancellationToken ct = default)
    {
        if (snapshotMode)
        {
            var swDuck = Stopwatch.StartNew();
            var cols = await _duckdb.GetTableColumnsAsync(datasetId, tableName);
            swDuck.Stop();
            await _debug.LogAsync(companyId, DebugLevel.Debug, "DataDocs",
                $"Read {cols.Count} column(s) from DuckDB snapshot for '{tableName}'.",
                datasetId: datasetId, tableName: tableName, durationMs: swDuck.ElapsedMilliseconds,
                context: new { source = "duckdb", columns = cols.Count }, ct: ct);
            return cols;
        }

        // Source mode: read only the column shape from the External dataset's live source. This is a
        // schema-only describe that never executes the query or transfers rows, so it stays fast even when
        // the source "table" is actually a heavy view (a plain SELECT * would force the source to evaluate
        // it just to hand back the first row).
        var dataset = await _db.Dataset.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == datasetId && d.CompanyId == companyId, ct);
        if (dataset?.SourceType != DatasetSourceType.External || string.IsNullOrWhiteSpace(dataset.SourceEntityId))
        {
            await _debug.LogAsync(companyId, DebugLevel.Warn, "DataDocs",
                $"No live columns for '{tableName}': dataset is not External or has no source entity.",
                datasetId: datasetId, tableName: tableName, ct: ct);
            return new List<Column>();
        }

        var swSource = Stopwatch.StartNew();
        var schema = await _dbTables.GetTableSchemaAsync(dataset.SourceEntityId!, companyId, tableName, ct);
        swSource.Stop();
        if (schema.Error != null)
        {
            await _debug.LogAsync(companyId, DebugLevel.Warn, "DataDocs",
                $"Failed to read live columns for '{tableName}' from source: {schema.Error}",
                datasetId: datasetId, tableName: tableName, durationMs: swSource.ElapsedMilliseconds,
                error: schema.Error, ct: ct);
            return new List<Column>();
        }

        await _debug.LogAsync(companyId, DebugLevel.Debug, "DataDocs",
            $"Read {schema.Columns.Count} column(s) from external source for '{tableName}'.",
            datasetId: datasetId, tableName: tableName, durationMs: swSource.ElapsedMilliseconds,
            context: new { source = "external", columns = schema.Columns.Count }, ct: ct);
        return schema.Columns;
    }

    private async Task UpsertAsync(string companyId, string datasetId, string tableName, bool snapshotMode, List<SaveColumnDocRequest> items, bool aiGenerated, string? userId, CancellationToken ct)
    {
        if (items == null || items.Count == 0) return;

        var existing = await _db.DatasetColumnDoc
            .Where(d => d.CompanyId == companyId && d.DatasetId == datasetId && d.TableName == tableName && d.IsSnapshot == snapshotMode)
            .ToListAsync(ct);
        var byName = existing.ToDictionary(d => d.ColumnName, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ColumnName)) continue;

            if (!byName.TryGetValue(item.ColumnName, out var doc))
            {
                doc = new DatasetColumnDoc
                {
                    Id = Guid.NewGuid().ToString(),
                    CompanyId = companyId,
                    DatasetId = datasetId,
                    TableName = tableName,
                    ColumnName = item.ColumnName,
                    IsSnapshot = snapshotMode,
                };
                _db.DatasetColumnDoc.Add(doc);
                byName[item.ColumnName] = doc;
            }

            doc.DisplayName = Trim(item.DisplayName, 200);
            doc.Description = Trim(item.Description, 1000);
            doc.SemanticType = Trim(item.SemanticType, 60);
            doc.Unit = Trim(item.Unit, 60);
            doc.IsPii = item.IsPii;
            doc.PiiType = Trim(item.PiiType, 60);
            doc.IsAiGenerated = aiGenerated;
            if (aiGenerated) doc.GeneratedAt = now;
            else doc.EditedBy = userId;
            doc.ModifiedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > max ? v.Substring(0, max) : v;
    }
}

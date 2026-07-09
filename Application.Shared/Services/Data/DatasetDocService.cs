using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Data;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

public interface IDatasetDocService
{
    /// <summary>
    /// Every live column of the table merged with its saved documentation (left-join on column name),
    /// so undocumented columns still appear with null doc fields. Live columns come from DuckDB.
    /// </summary>
    Task<TableDocDto> GetTableDocsAsync(string companyId, string datasetId, string tableName, CancellationToken ct = default);

    /// <summary>Upserts user edits to column docs (manual — clears the AI-generated flag).</summary>
    Task<TableDocDto> SaveColumnDocsAsync(string companyId, string datasetId, string tableName, string? userId, List<SaveColumnDocRequest> edits, CancellationToken ct = default);

    /// <summary>Upserts AI-generated column docs (sets is_ai_generated + generated_at). Used by the generator.</summary>
    Task ApplyGeneratedDocsAsync(string companyId, string datasetId, string tableName, List<SaveColumnDocRequest> generated, CancellationToken ct = default);
}

/// <summary>
/// CRUD for the semantic layer — per-column documentation of dataset tables. Company- (and dataset/
/// table-) scoped. Column structure is read live from DuckDB (<see cref="IDuckdbService"/>); this
/// service only manages the documentation overlay persisted in <c>dataset_column_doc</c>.
/// </summary>
public class DatasetDocService : IDatasetDocService
{
    private readonly ApplicationDbContext _db;
    private readonly IDuckdbService _duckdb;

    public DatasetDocService(ApplicationDbContext db, IDuckdbService duckdb)
    {
        _db = db;
        _duckdb = duckdb;
    }

    public async Task<TableDocDto> GetTableDocsAsync(string companyId, string datasetId, string tableName, CancellationToken ct = default)
    {
        var columns = await _duckdb.GetTableColumnsAsync(datasetId, tableName);

        var docs = await _db.DatasetColumnDoc.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.DatasetId == datasetId && d.TableName == tableName)
            .ToListAsync(ct);
        var byName = docs.ToDictionary(d => d.ColumnName, StringComparer.OrdinalIgnoreCase);

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

    public async Task<TableDocDto> SaveColumnDocsAsync(string companyId, string datasetId, string tableName, string? userId, List<SaveColumnDocRequest> edits, CancellationToken ct = default)
    {
        await UpsertAsync(companyId, datasetId, tableName, edits, aiGenerated: false, userId, ct);
        return await GetTableDocsAsync(companyId, datasetId, tableName, ct);
    }

    public Task ApplyGeneratedDocsAsync(string companyId, string datasetId, string tableName, List<SaveColumnDocRequest> generated, CancellationToken ct = default)
        => UpsertAsync(companyId, datasetId, tableName, generated, aiGenerated: true, userId: null, ct);

    private async Task UpsertAsync(string companyId, string datasetId, string tableName, List<SaveColumnDocRequest> items, bool aiGenerated, string? userId, CancellationToken ct)
    {
        if (items == null || items.Count == 0) return;

        var existing = await _db.DatasetColumnDoc
            .Where(d => d.CompanyId == companyId && d.DatasetId == datasetId && d.TableName == tableName)
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

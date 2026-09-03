using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

/// <summary>
/// Read-only projections of a company's datasets, data catalog, per-user access, RLS and credentials into
/// the exact shapes the external chat app consumes. Enforces: company scope, per-user dataset visibility
/// (via <see cref="IDatasetService"/>), table access (DatasetUserTable), column access (DatasetUserColumn)
/// and returns RLS filters (UserRlsFilter) for the consumer to apply. Backs the API-key public controllers.
/// </summary>
public interface IPublicDatasetApiService
{
    Task<List<PublicDatasetDto>> GetUserDatasetsAsync(string companyId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Creates the acting user's personal ("user") dataset and returns it ready to open. Always inserts a
    /// new row — callers that want at-most-one should check <see cref="GetUserDatasetAsync"/> first.
    /// </summary>
    Task<PublicDatasetDto> CreateUserDatasetAsync(string companyId, string userId, CreateUserDatasetRequest request, CancellationToken ct = default);

    /// <summary>The acting user's personal dataset, or null when they don't have one yet.</summary>
    Task<PublicDatasetDto?> GetUserDatasetAsync(string companyId, string userId, CancellationToken ct = default);
    Task<DataCatalogDto?> GetDataCatalogAsync(string companyId, string userId, string datasetId, CancellationToken ct = default);
    Task<List<UserTableAccessDto>> GetUserTableAccessAsync(string companyId, string userId, string datasetId, CancellationToken ct = default);
    Task<List<UserColumnAccessDto>> GetUserColumnAccessAsync(string companyId, string userId, string datasetId, CancellationToken ct = default);
    Task<List<UserRlsFilterDto>> GetUserRlsAsync(string companyId, string userId, string datasetId, CancellationToken ct = default);
    Task<DatasetCredentialDto?> GetCredentialAsync(string companyId, string userId, string datasetId, CancellationToken ct = default);
}

public class PublicDatasetApiService : IPublicDatasetApiService
{
    private const int DuckDbType = 1; // chat DatasetType.DUCKDB

    private readonly ApplicationDbContext _db;
    private readonly IDatasetService _datasets;
    private readonly IDuckdbService _duckdb;
    private readonly IDatabaseTableService _dbTables;
    private readonly IDatasetDocService _docs;
    private readonly DuckdbOption _duckdbOption;

    public PublicDatasetApiService(
        ApplicationDbContext db,
        IDatasetService datasets,
        IDuckdbService duckdb,
        IDatabaseTableService dbTables,
        IDatasetDocService docs,
        DuckdbOption duckdbOption)
    {
        _db = db;
        _datasets = datasets;
        _duckdb = duckdb;
        _dbTables = dbTables;
        _docs = docs;
        _duckdbOption = duckdbOption;
    }

    public async Task<List<PublicDatasetDto>> GetUserDatasetsAsync(string companyId, string userId, CancellationToken ct = default)
    {
        var datasets = await _datasets.GetDatasetsByCompanyAsync(companyId, userId);

        // Only surface datasets that have data docs — i.e. at least one documented table for the dataset's
        // mode (Local reads snapshot docs, External reads live-source docs). Keeps the list consistent with
        // what the catalog will actually return.
        var documentedDatasetKeys = (await _db.DatasetColumnDoc
                .Where(d => d.CompanyId == companyId)
                .Select(d => new { d.DatasetId, d.IsSnapshot })
                .Distinct()
                .ToListAsync(ct))
            .Select(x => $"{x.DatasetId}|{x.IsSnapshot}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        datasets = datasets
            .Where(d => documentedDatasetKeys.Contains($"{d.Id}|{d.SourceType != DatasetSourceType.External}"))
            .ToList();

        // One lookup gives entityId -> engine for every connected external DB, so we can map the chat's
        // numeric `type` without a per-dataset cross-context query. Our (reordered) DataSourceType aligns
        // 1:1 with the chat's DatasetType for these engines, so (int)DatabaseType is the value.
        var engineByEntity = (await _dbTables.GetConnectedDatabasesAsync(companyId, ct))
            .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().DatabaseType);

        var defaultDatasetId = await _db.UserDefaultDataset
            .Where(d => d.CompanyId == companyId && d.UserId == userId)
            .Select(d => d.DatasetId)
            .FirstOrDefaultAsync(ct);

        return datasets.Select(d => new PublicDatasetDto
        {
            Id = d.Id ?? string.Empty,
            Name = d.Name ?? string.Empty,
            Description = d.Description ?? string.Empty,
            CompanyId = d.CompanyId ?? companyId,
            Type = ResolveType(d, engineByEntity),
            IsDefault = !string.IsNullOrEmpty(defaultDatasetId) && d.Id == defaultDatasetId,
            IsMessageDataset = false,
            IsDeleted = false,
            // host/port/username/password/driver intentionally empty (sourced only when connecting).
        }).ToList();
    }

    // Fallbacks when the caller sends blanks, so a personal dataset is always openable.
    private const string DefaultUserDatasetDescription = "this is your personal dataset";
    private const string DefaultUserDatasetPath = "E://duckdb/chat";

    public async Task<PublicDatasetDto> CreateUserDatasetAsync(string companyId, string userId, CreateUserDatasetRequest request, CancellationToken ct = default)
    {
        // Inserted straight through the DbContext rather than via DatasetService.CreateDatasetAsync on
        // purpose: that path enforces the Dataset.Name regex (letters/digits/spaces only) and per-company
        // name uniqueness, which would reject usernames containing '.' or '@' and refuse a second personal
        // dataset. EF Core doesn't evaluate the model's validation attributes on save, so a direct insert
        // accepts the username verbatim.
        var now = DateTime.UtcNow;
        var dataset = new Dataset
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? DefaultUserDatasetDescription
                : request.Description.Trim(),
            SourceType = DatasetSourceType.Local,
            SourceEntityId = null,
            Path = string.IsNullOrWhiteSpace(request.Path) ? DefaultUserDatasetPath : request.Path.Trim(),
            IsUserDataset = true,
            CreatedBy = userId,
            CreatedAt = now,
            ModifiedAt = now
            // The `type` column has no mapped property, so it takes its DB default of 0.
        };

        _db.Dataset.Add(dataset);
        await _db.SaveChangesAsync(ct);

        // DatasetService.CreateDatasetAsync would also create the DuckDB file here. Do it too, or the
        // directory won't exist and the consumer's first open of Host fails (DuckDB creates a missing file
        // but not a missing folder). Best-effort: the row is already committed, so a storage problem must
        // not surface as a failed create — the caller would retry and duplicate the dataset.
        // No DatasetUser row is needed: CreatedBy == userId already grants owner access in
        // DatasetService.GetDatasetAsync/GetAccessibleTablesAsync, which the other public endpoints use.
        try { await _duckdb.EnsureDatabaseAsync(dataset.Id!); } catch { /* surfaced on first use instead */ }

        return ToUserDatasetDto(dataset);
    }

    public async Task<PublicDatasetDto?> GetUserDatasetAsync(string companyId, string userId, CancellationToken ct = default)
    {
        // Deliberately not GetUserDatasetsAsync: that filters to datasets with data docs, and a freshly
        // provisioned personal dataset has none yet but must still be returned.
        var dataset = await _db.Dataset.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.CreatedBy == userId && d.IsUserDataset)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return dataset == null ? null : ToUserDatasetDto(dataset);
    }

    /// <summary>
    /// Maps a personal dataset into the shape the chat app opens directly: the DuckDB file path goes in
    /// <c>Host</c> and the engine is reported as DuckDB, because the consumer never re-fetches credentials
    /// for it. Path construction mirrors <see cref="GetCredentialAsync"/>'s Local branch and DuckdbService's
    /// own resolution, so this points at the same file the backend would open.
    /// </summary>
    private PublicDatasetDto ToUserDatasetDto(Dataset d)
    {
        var dir = string.IsNullOrWhiteSpace(d.Path) ? _duckdbOption.DuckdbFilePath : d.Path!;
        return new PublicDatasetDto
        {
            Id = d.Id ?? string.Empty,
            Name = d.Name ?? string.Empty,
            Description = d.Description ?? string.Empty,
            CompanyId = d.CompanyId ?? string.Empty,
            Type = DuckDbType,
            // Trim() as well as the separator: DuckdbService normalises a user-typed path the same way, and
            // a trailing space would otherwise point Host at a file the backend never wrote.
            Host = $"{(dir ?? string.Empty).Trim().TrimEnd('/', '\\')}/{d.Id}.duckdb",
            Port = 0,
            Username = string.Empty,
            Password = string.Empty,
            Driver = string.Empty,
            IsDefault = false,
            IsMessageDataset = true,
            IsDeleted = false
        };
    }

    private static int ResolveType(Dataset d, IReadOnlyDictionary<string, DataSourceType> engineByEntity)
        => d.SourceType == DatasetSourceType.External
           && !string.IsNullOrWhiteSpace(d.SourceEntityId)
           && engineByEntity.TryGetValue(d.SourceEntityId!, out var engine)
            ? (int)engine
            : DuckDbType;

    public async Task<DataCatalogDto?> GetDataCatalogAsync(string companyId, string userId, string datasetId, CancellationToken ct = default)
    {
        var dataset = await _datasets.GetDatasetAsync(datasetId, userId);
        if (dataset == null || dataset.CompanyId != companyId) return null;

        var snapshotMode = dataset.SourceType != DatasetSourceType.External; // Local→DuckDB; External→live source
        var tables = await ListAccessibleTablesAsync(userId, dataset, ct);

        // Only include tables that have data docs for this mode (documented in DatasetColumnDoc). Curated
        // tables only — undocumented tables are hidden from the catalog even if the user can access them.
        var documentedTables = (await _db.DatasetColumnDoc
                .Where(d => d.CompanyId == companyId && d.DatasetId == datasetId && d.IsSnapshot == snapshotMode)
                .Select(d => d.TableName)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        tables = tables.Where(t => documentedTables.Contains(t)).ToList();

        // Per-user column restrictions for this dataset: table -> allowed column set (absent = all columns).
        var columnRestrictions = (await _db.DatasetUserColumn
                .Where(c => c.CompanyId == companyId && c.UserId == userId && c.DatasetId == datasetId)
                .ToListAsync(ct))
            .GroupBy(c => c.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase),
                          StringComparer.OrdinalIgnoreCase);

        var catalog = new DataCatalogDto();
        foreach (var table in tables)
        {
            // Live columns carry the real structural flags (nullable / primary key / type); saved docs carry
            // the human descriptions. Merging both gives the AI usable column metadata, not stubbed defaults.
            List<Column> liveColumns;
            Dictionary<string, ColumnDocDto> savedDocs;
            try
            {
                liveColumns = await _docs.GetLiveColumnsAsync(companyId, datasetId, table, snapshotMode, ct);
                savedDocs = await _docs.GetSavedColumnDocsAsync(companyId, datasetId, table, snapshotMode, ct);
            }
            catch { continue; } // unreadable/locked table → skip rather than fail the whole catalog

            IEnumerable<Column> columns = liveColumns;
            if (columnRestrictions.TryGetValue(table, out var allowed))
                columns = columns.Where(c => allowed.Contains(c.Name));

            catalog.TableMetadata.Add(new TableMetadataDto
            {
                DatasetId = datasetId,
                TableName = table,
                TableDescription = string.Empty, // no table-level description stored in this backend yet
                CompanyId = companyId,
                Columns = columns.Select(c =>
                {
                    savedDocs.TryGetValue(c.Name, out var doc);
                    return new ColumnMetadataDto
                    {
                        DatasetId = datasetId,
                        TableName = table,
                        ColumnName = c.Name,
                        ColumnDescription = doc?.Description ?? string.Empty,
                        DataType = c.DataType ?? string.Empty,
                        MaxLength = null,                       // not captured by the current schema reads
                        IsNullable = c.IsNullable,              // real for DuckDB/local; defaults true for live external
                        IsPrimaryKey = c.IsPrimaryKey ?? false, // real for DuckDB/local; false for live external
                        TableRelations = new(),                 // no FK/relationship catalog in this backend yet
                        // The rest of the semantic layer. Already loaded above — only Description used to
                        // be read. SemanticType/Unit are what keep an AI consumer from aggregating a
                        // percentage or an identifier; IsPii is advisory and must not be treated as access
                        // control (see the ColumnMetadataDto remarks).
                        DisplayName = doc?.DisplayName,
                        SemanticType = doc?.SemanticType,
                        Unit = doc?.Unit,
                        IsPii = doc?.IsPii ?? false,
                        PiiType = doc?.PiiType
                    };
                }).ToList()
            });
        }

        return catalog;
    }

    public async Task<List<UserTableAccessDto>> GetUserTableAccessAsync(string companyId, string userId, string datasetId, CancellationToken ct = default)
    {
        var dataset = await _datasets.GetDatasetAsync(datasetId, userId);
        if (dataset == null || dataset.CompanyId != companyId) return new();

        var tables = await ListAccessibleTablesAsync(userId, dataset, ct);

        var columnRows = await _db.DatasetUserColumn
            .Where(c => c.CompanyId == companyId && c.UserId == userId && c.DatasetId == datasetId)
            .ToListAsync(ct);
        var columnsByTable = columnRows
            .GroupBy(c => c.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return tables.Select(table =>
        {
            columnsByTable.TryGetValue(table, out var cols);
            return new UserTableAccessDto
            {
                UserId = userId,
                DatasetId = datasetId,
                TableName = table,
                CompanyId = companyId,
                // No column-restriction rows for this table => all columns visible.
                HasFullAccess = cols == null || cols.Count == 0,
                ColumnAccess = (cols ?? new()).Select(c => new UserColumnAccessDto
                {
                    UserId = userId,
                    DatasetId = datasetId,
                    TableName = table,
                    ColumnName = c.ColumnName,
                    CompanyId = companyId
                }).ToList()
            };
        }).ToList();
    }

    public async Task<List<UserColumnAccessDto>> GetUserColumnAccessAsync(string companyId, string userId, string datasetId, CancellationToken ct = default)
    {
        return await _db.DatasetUserColumn
            .Where(c => c.CompanyId == companyId && c.UserId == userId && c.DatasetId == datasetId)
            .Select(c => new UserColumnAccessDto
            {
                UserId = c.UserId,
                DatasetId = c.DatasetId,
                TableName = c.TableName,
                ColumnName = c.ColumnName,
                CompanyId = c.CompanyId
            })
            .ToListAsync(ct);
    }

    public async Task<List<UserRlsFilterDto>> GetUserRlsAsync(string companyId, string userId, string datasetId, CancellationToken ct = default)
    {
        return await _db.UserRlsFilter
            .Where(r => r.CompanyId == companyId && r.UserId == userId && r.DatasetId == datasetId)
            .Select(r => new UserRlsFilterDto
            {
                UserId = r.UserId,
                DatasetId = r.DatasetId,
                TableName = r.TableName,
                ColumnName = r.ColumnName,
                AllowedValues = r.AllowedValues,
                CompanyId = r.CompanyId
            })
            .ToListAsync(ct);
    }

    public async Task<DatasetCredentialDto?> GetCredentialAsync(string companyId, string userId, string datasetId, CancellationToken ct = default)
    {
        var dataset = await _datasets.GetDatasetAsync(datasetId, userId);
        if (dataset == null || dataset.CompanyId != companyId) return null;

        // Local dataset: no external DB connection — hand back the DuckDB file location + engine so a
        // co-located consumer can open it directly (a remote consumer should route queries via the backend).
        if (dataset.SourceType != DatasetSourceType.External || string.IsNullOrWhiteSpace(dataset.SourceEntityId))
        {
            var dir = string.IsNullOrWhiteSpace(dataset.Path) ? _duckdbOption.DuckdbFilePath : dataset.Path!;
            return new DatasetCredentialDto
            {
                Id = datasetId,
                DatasetId = datasetId,
                Name = dataset.Name ?? string.Empty,
                Type = DuckDbType,
                FilePath = $"{(dir ?? string.Empty).TrimEnd('/', '\\')}/{datasetId}.duckdb"
            };
        }

        var conn = await _dbTables.GetDecryptedConnectionAsync(dataset.SourceEntityId!, companyId, ct);
        if (conn == null) return null;

        return new DatasetCredentialDto
        {
            Id = conn.Id,
            DatasetId = datasetId,
            Name = dataset.Name ?? string.Empty,
            Type = (int)conn.DatabaseType, // aligns 1:1 with the chat's DatasetType for these engines
            Host = conn.Host ?? string.Empty,
            Port = conn.Port,
            DatabaseName = conn.DatabaseName ?? string.Empty,
            UseSsl = conn.UseSsl,
            Username = conn.Username ?? string.Empty,
            Password = conn.SecretEncrypted ?? string.Empty, // decrypted in-memory by GetDecryptedConnectionAsync
            FilePath = conn.FilePath ?? string.Empty,         // set for a DuckDB-file external source
            ApiKey = null,
            ConnectionString = null
        };
    }

    /// <summary>Table names of a dataset the given user may access (table-level grants applied).</summary>
    private async Task<List<string>> ListAccessibleTablesAsync(string userId, Dataset dataset, CancellationToken ct)
    {
        List<string> tables;
        if (dataset.SourceType == DatasetSourceType.External && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
        {
            var discovery = await _dbTables.DiscoverTablesAsync(dataset.SourceEntityId!, dataset.CompanyId!, ct);
            tables = discovery.Tables.Select(t => t.FullName).ToList();
        }
        else
        {
            tables = (await _duckdb.GetTablesAsync(dataset.Id!)).ToList();
        }

        // null => full access to all tables; otherwise restrict to the granted set.
        var accessible = await _datasets.GetAccessibleTablesAsync(dataset.Id!, userId);
        if (accessible != null)
            tables = tables.Where(t => accessible.Contains(t)).ToList();

        return tables;
    }
}

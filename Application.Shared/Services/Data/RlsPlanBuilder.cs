using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

/// <summary>
/// Turns this app's grant tables into one acting user's effective access to one dataset, in the form both
/// enforcement modes need.
/// </summary>
/// <remarks>
/// Shared on purpose. The provisioner writes it into the source as roles and policies; the rewriter turns
/// it into secured subqueries. If they computed access separately, the same grant could come to mean two
/// different things depending on which mode a source happened to be in — and a difference like that
/// surfaces as a leak on one path only, which is the hardest kind to notice.
/// </remarks>
public interface IRlsPlanBuilder
{
    /// <summary>
    /// Builds the plan, or null when the dataset is not visible to this user or has no external source.
    /// </summary>
    Task<RlsProvisioningPlan?> BuildAsync(string companyId, string datasetId, string userId,
        CancellationToken ct = default);

    /// <summary>Every (dataset, user) pair on one source that has something to provision.</summary>
    Task<List<(string DatasetId, string UserId)>> ListGrantedPairsAsync(string companyId,
        string sourceEntityId, CancellationToken ct = default);
}

public class RlsPlanBuilder : IRlsPlanBuilder
{
    private readonly ApplicationDbContext _db;
    private readonly IDatasetService _datasets;
    private readonly IDatasetDocService _docs;

    public RlsPlanBuilder(ApplicationDbContext db, IDatasetService datasets, IDatasetDocService docs)
    {
        _db = db;
        _datasets = datasets;
        _docs = docs;
    }

    public async Task<RlsProvisioningPlan?> BuildAsync(string companyId, string datasetId, string userId,
        CancellationToken ct = default)
    {
        var dataset = await _datasets.GetDatasetAsync(datasetId, userId);
        if (dataset == null
            || !string.Equals(dataset.CompanyId, companyId, StringComparison.Ordinal)
            || dataset.SourceType != DatasetSourceType.External
            || string.IsNullOrWhiteSpace(dataset.SourceEntityId))
        {
            return null;
        }

        // The documented live tables — the same set the catalog advertises and the query endpoint allows,
        // so "what the user was told exists" and "what gets provisioned" cannot drift apart.
        var documented = await _docs.GetDocumentedTablesAsync(companyId, datasetId, snapshotMode: false, ct);

        // Table grants: null means every table (owner, admin share, or a share with no table restriction).
        var accessible = await _datasets.GetAccessibleTablesAsync(datasetId, userId);

        var tables = documented
            .Where(t => accessible == null || accessible.Contains(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var columnGrants = (await _db.DatasetUserColumn.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.UserId == userId && c.DatasetId == datasetId)
                .ToListAsync(ct))
            .GroupBy(c => c.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.ColumnName).Distinct().OrderBy(n => n).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var rlsRows = await _db.UserRlsFilter.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.UserId == userId && r.DatasetId == datasetId)
            .ToListAsync(ct);

        var plans = new List<RlsTablePlan>();
        foreach (var table in tables)
        {
            var filters = rlsRows
                .Where(r => r.AppliesTo(table))
                .Select(r => new RlsFilterPlan(r.ColumnName, r.GetAllowedValuesList(), r.AppliesToAllTables))
                .ToList();

            plans.Add(new RlsTablePlan(
                CatalogTable: table,
                GrantedColumns: columnGrants.TryGetValue(table, out var cols) ? cols : null,
                Filters: filters));
        }

        return new RlsProvisioningPlan
        {
            CompanyId = companyId,
            SourceEntityId = dataset.SourceEntityId!,
            DatasetId = datasetId,
            UserId = userId,
            RoleName = RlsNaming.RoleName(datasetId, userId),
            Tables = plans
        };
    }

    public async Task<List<(string DatasetId, string UserId)>> ListGrantedPairsAsync(string companyId,
        string sourceEntityId, CancellationToken ct = default)
    {
        var datasets = await _db.Dataset.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                        && d.SourceType == DatasetSourceType.External
                        && d.SourceEntityId == sourceEntityId)
            .Select(d => new { Id = d.Id!, d.CreatedBy })
            .ToListAsync(ct);

        if (datasets.Count == 0) return new();

        var datasetIds = datasets.Select(d => d.Id).ToList();

        // Anyone the dataset is shared with needs a role on the Native path — not only restricted users.
        // The query account holds no access of its own, so an unrestricted user without a role would be
        // refused everything rather than allowed everything.
        //
        // DatasetUser carries no CompanyId; it is company-scoped through its dataset, and datasetIds
        // above is already filtered by company.
        var pairs = (await _db.DatasetUser.AsNoTracking()
                .Where(u => datasetIds.Contains(u.DatasetId))
                .Select(u => new { u.DatasetId, u.UserId })
                .Distinct()
                .ToListAsync(ct))
            .Select(p => (p.DatasetId, p.UserId))
            .ToHashSet();

        // The creator has access without a share row — GetDatasetAsync matches on creator OR a
        // DatasetUser row — so omitting them would provision every user except the dataset's author.
        foreach (var dataset in datasets.Where(d => !string.IsNullOrWhiteSpace(d.CreatedBy)))
            pairs.Add((dataset.Id, dataset.CreatedBy!));

        return pairs.ToList();
    }
}

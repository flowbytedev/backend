using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

/// <inheritdoc cref="IDatasetLiveSourceService"/>
public class DatasetLiveSourceService : IDatasetLiveSourceService
{
    private readonly ApplicationDbContext _db;
    private readonly IDatasetService _datasets;

    public DatasetLiveSourceService(ApplicationDbContext db, IDatasetService datasets)
    {
        _db = db;
        _datasets = datasets;
    }

    public async Task<LiveSourceRead> ResolveReadAsync(string companyId, string datasetId, string? actingUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId) || string.IsNullOrWhiteSpace(datasetId))
            return Refuse(LiveSourceReadOutcome.DatasetNotFound, $"Dataset '{datasetId}' not found.");

        // Company-scoped, NOT user-scoped: the key carries the tenant and the acting user is optional on
        // this surface, so a per-user share check here would 404 every keyless-user request.
        var dataset = await _datasets.GetDatasetForCompanyAsync(datasetId, companyId);
        if (dataset == null)
            return Refuse(LiveSourceReadOutcome.DatasetNotFound, $"Dataset '{datasetId}' not found.");

        if (dataset.SourceType != DatasetSourceType.External || string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            return Refuse(LiveSourceReadOutcome.NotExternalSource,
                $"Dataset '{datasetId}' is not backed by an external database source, so it has no live layer to read. " +
                "Omit 'source' (or send source=snapshot) to read its local tables.");

        HashSet<string>? allowedTables = null;

        if (!string.IsNullOrWhiteSpace(actingUserId))
        {
            var userId = actingUserId.Trim();

            // Fail closed. A live source table is "{schema}.{name}", which SecuredSqlBuilder's secured-
            // relation rewrite cannot shadow, so neither column masking nor RLS can be applied to these
            // rows. Serving them unmasked because this endpoint happens not to rewrite SQL would hand a
            // restricted user exactly the columns and rows someone took the trouble to restrict.
            var hasColumnGrants = await _db.DatasetUserColumn
                .AnyAsync(c => c.CompanyId == companyId && c.UserId == userId && c.DatasetId == datasetId, ct);
            var hasRowFilters = await _db.UserRlsFilter
                .AnyAsync(r => r.CompanyId == companyId && r.UserId == userId && r.DatasetId == datasetId, ct);

            if (hasColumnGrants || hasRowFilters)
            {
                var kinds = (hasColumnGrants, hasRowFilters) switch
                {
                    (true, true) => "column grants and row-level security filters",
                    (true, false) => "column grants",
                    _ => "row-level security filters"
                };
                return Refuse(LiveSourceReadOutcome.SecurityNotEnforceable,
                    $"User '{userId}' has {kinds} on dataset '{datasetId}', which cannot be enforced against a " +
                    "live external source. Read the snapshot instead (omit 'source'), or run the query through " +
                    "POST api/dataset/{datasetId}/query/run, which enforces both.");
            }

            // null = every table; empty = no access to the dataset at all.
            allowedTables = await _datasets.GetAccessibleTablesAsync(datasetId, userId);
            if (allowedTables != null && allowedTables.Count == 0)
                return Refuse(LiveSourceReadOutcome.UserNotPermitted,
                    $"User '{userId}' has no access to dataset '{datasetId}'.");
        }

        return new LiveSourceRead
        {
            Outcome = LiveSourceReadOutcome.Allowed,
            SourceEntityId = dataset.SourceEntityId,
            AllowedTables = allowedTables
        };
    }

    private static LiveSourceRead Refuse(LiveSourceReadOutcome outcome, string error) =>
        new() { Outcome = outcome, Error = error };
}

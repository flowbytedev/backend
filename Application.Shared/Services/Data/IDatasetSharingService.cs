using Application.Shared.Models;

namespace Application.Shared.Services.Data;

public interface IDatasetSharingService
{
    Task<List<DatasetUserDto>> GetDatasetUsersAsync(string datasetId);
    Task<bool> ShareDatasetAsync(ShareDatasetRequest request, string sharedByUserId);

    /// <summary>Additively grants a single table to a user. Creates the share (restricted to that table)
    /// if the user has no access yet; never reduces a user who already has full dataset access.</summary>
    Task<bool> GrantTableAccessAsync(GrantTableShareRequest request, string sharedByUserId);
    Task<bool> UpdateDatasetUserTypeAsync(string datasetId, string userId, DatasetUserType userType);
    Task<bool> RemoveDatasetUserAsync(string datasetId, string userId);

    /// <summary>Removes one table from a table-scoped user's access. If it was their only scoped table the
    /// whole share is removed. Returns false for a user with full dataset access (no single-table scope to
    /// revoke) or when the user isn't scoped to that table.</summary>
    Task<bool> RevokeTableAccessAsync(string datasetId, string userId, string tableName);
    Task<List<Dataset>> GetSharedDatasetsAsync(string userId, string companyId);
    Task<bool> HasDatasetAccessAsync(string datasetId, string userId, DatasetUserType? requiredType = null);

    /// <summary>Number of users each dataset is shared with (count of dataset-level share rows), keyed by
    /// dataset id. Datasets shared with nobody are omitted.</summary>
    Task<Dictionary<string, int>> GetDatasetShareCountsAsync(IEnumerable<string> datasetIds, CancellationToken ct = default);

    /// <summary>Number of users who can access each of the given tables, keyed by table name. A user with
    /// full dataset access counts for every table; a table-scoped user counts only for their scoped tables.</summary>
    Task<Dictionary<string, int>> GetTableShareCountsAsync(string datasetId, IEnumerable<string> tableNames, CancellationToken ct = default);

    /// <summary>The current per-user access to a dataset (type + tables + columns + RLS), for the data-admin
    /// User Access editor. <see cref="UserDatasetAccessDto.HasAccess"/> is false when the user has no share.</summary>
    Task<UserDatasetAccessDto> GetUserDatasetAccessAsync(string datasetId, string userId);

    /// <summary>Atomically applies a user's full access to a dataset by user id (upserts the share + reconciles
    /// table, column and RLS scope). When <see cref="SetUserAccessRequest.Remove"/> is true, all access is removed.</summary>
    Task<bool> SetUserDatasetAccessAsync(string companyId, string datasetId, string userId, SetUserAccessRequest request);
}

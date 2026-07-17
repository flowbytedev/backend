using Application.Shared.Models;
using Application.Shared.Models.Data;

namespace Application.Shared.Services.Data;


public interface IDatasetService
{
    Task<Dataset?> GetDatasetAsync(string id, string userId);

    /// <summary>
    /// Resolves which tables a user may access within a dataset. Returns <c>null</c> when the user has
    /// access to ALL tables (dataset owner, a dataset-Admin share, or a share with no table restriction).
    /// Returns a (case-insensitive) set when the user is restricted to specific tables, or an empty set
    /// when the user has no access at all.
    /// </summary>
    Task<HashSet<string>?> GetAccessibleTablesAsync(string datasetId, string userId);
    Task<List<Dataset>> GetDatasetsByCompanyAsync(string companyId, string userId);
    Task<List<Dataset>> GetDatasetsAsync(string userId);
    Task<Dataset?> CreateDatasetAsync(Dataset dataset, string userId);
    Task<Dataset?> UpdateDatasetAsync(string id, Dataset dataset, string userId);
    Task<bool> DeleteDatasetAsync(string id, string userId);

    /// <summary>
    /// True when <paramref name="name"/> is not already used by another dataset in the company (names must
    /// be unique per company, case-insensitive). Pass <paramref name="excludeId"/> when editing so the
    /// dataset's own current name doesn't count as a clash. An empty name is treated as unavailable.
    /// </summary>
    Task<bool> IsNameAvailableAsync(string companyId, string name, string? excludeId = null);

    // New methods for chat functionality
    Task<List<Dataset>> GetDatasetsByIdsAsync(List<string> datasetIds, string companyId, string userId);
    Task<List<Dataset>> SearchDatasetsAsync(string query, string companyId, string userId);
    
    // Table-level methods for chat functionality
    Task<List<TableSearchResult>> SearchTablesAsync(string query, string companyId, string userId);
    Task<TableReference?> GetTableWithDataAsync(string datasetId, string tableName, string companyId, string userId, int sampleRows = 10);
    Task<List<TableReference>> GetTablesByReferencesAsync(List<TableReference> tableReferences, string companyId, string userId, int sampleRows = 10);
}

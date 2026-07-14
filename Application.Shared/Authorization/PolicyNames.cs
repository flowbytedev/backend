namespace Application.Shared.Authorization;

/// <summary>
/// Canonical authorization policy names, shared by the server (controllers) and the
/// WASM client (page <c>[Authorize(Policy = ...)]</c> attributes) so the two never drift.
/// </summary>
public static class PolicyNames
{
    public const string DatasetsAccess = "DatasetsAccess";
    /// <summary>Query Workbench + Query Notebook page/API access (QUERY role, ADMIN always).</summary>
    public const string QueryAccess = "QueryAccess";
    /// <summary>Ingestion + Data Docs page/API access (DATA_ADMIN role, ADMIN always).</summary>
    public const string DataAdminAccess = "DataAdminAccess";
    /// <summary>Shared read access to the datasets/tables API — any data-module role (DATASETS, QUERY,
    /// DATA_ADMIN, or ADMIN) can list datasets/tables; write actions still enforce EDIT_DATA.</summary>
    public const string DataReadAccess = "DataReadAccess";
    public const string DataWarehouseRead = "DataWarehouseRead";
    public const string MetricsRead = "MetricsRead";
    public const string MetricsWrite = "MetricsWrite";
    public const string SalesRead = "SalesRead";
    public const string StatusRead = "StatusRead";
    public const string StatusWrite = "StatusWrite";
    public const string InventoryRead = "InventoryRead";
    public const string DashboardsRead = "DashboardsRead";
    public const string DataLogRead = "DataLogRead";
}

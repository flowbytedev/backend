namespace Application.Shared.Authorization;

/// <summary>
/// Canonical authorization policy names, shared by the server (controllers) and the
/// WASM client (page <c>[Authorize(Policy = ...)]</c> attributes) so the two never drift.
/// </summary>
public static class PolicyNames
{
    public const string DatasetsAccess = "DatasetsAccess";
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

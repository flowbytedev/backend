namespace Application.Shared.Authorization;

/// <summary>
/// Role-name suffixes. Actual role claims are company-prefixed, e.g. <c>ACME_METRICS_READ</c>.
/// Use <see cref="Role"/> to build a full role name so the UI and the authorization handler
/// produce identical strings.
/// </summary>
public static class RoleSuffixes
{
    public const string Admin = "ADMIN";
    public const string Datasets = "DATASETS";
    public const string DataWarehouse = "DATA_WAREHOUSE";
    public const string MetricsRead = "METRICS_READ";
    public const string MetricsWrite = "METRICS_WRITE";
    public const string Sales = "SALES";
    public const string StatusRead = "STATUS_READ";
    public const string Incidents = "INCIDENTS";
    public const string InventoryRead = "INVENTORY_READ";
    public const string DashboardsRead = "DASHBOARDS_READ";

    /// <summary>Grants access to the Datasets/Tables audit log viewer (ADMIN also passes).</summary>
    public const string DataAdmin = "DATA_ADMIN";

    /// <summary>Builds the full, company-prefixed role name (e.g. <c>ACME_METRICS_READ</c>).</summary>
    public static string Role(string companyId, string suffix) => $"{companyId}_{suffix}";
}

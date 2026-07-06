namespace Application.Dashboard.Configuration;

/// <summary>
/// ClickHouse connection to the <c>sales_dataset</c> warehouse that powers the sales dashboards,
/// bound from the <c>SalesDataset</c> appsettings section. A single shared connection; multi-tenancy
/// is enforced by the <c>company_id</c> filter in every query (same model as Daily Inventory).
/// </summary>
public class SalesDatasetSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 8123;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public bool UseSSL { get; set; }
}

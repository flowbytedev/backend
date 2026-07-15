namespace Application.Shared.Models;

/// <summary>
/// Per-company application settings (one row per company). Currently holds the debug-logging toggle
/// that a <c>{companyId}_ADMIN</c> flips to start capturing debug entries into the data_app_log store.
/// </summary>
public class CompanySettings : BaseModel
{
    public int Id { get; set; }

    /// <summary>When true, feature code emits debug log entries for this company (see IDebugLogService).</summary>
    public bool DebugLoggingEnabled { get; set; }
}

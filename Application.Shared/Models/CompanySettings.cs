namespace Application.Shared.Models;

/// <summary>
/// Per-company application settings (one row per company). Holds the debug-logging toggle that a
/// <c>{companyId}_ADMIN</c> flips to start capturing debug entries into the data_app_log store, and the
/// date format used for CSV exports.
/// </summary>
public class CompanySettings : BaseModel
{
    public int Id { get; set; }

    /// <summary>When true, feature code emits debug log entries for this company (see IDebugLogService).</summary>
    public bool DebugLoggingEnabled { get; set; }

    /// <summary>
    /// Date pattern for CSV exports, one of <see cref="ExportDateFormats.Allowed"/>. Null when the company
    /// has never chosen one — read it through <see cref="ExportDateFormats.Resolve"/> to get the default.
    /// </summary>
    public string? ExportDateFormat { get; set; }

    /// <summary>
    /// Whether this company's pipelines are checked against their freshness policies. Null means never
    /// chosen, which reads as enabled.
    /// <para>
    /// Subordinate to <c>Pipelines:FreshnessChecksEnabled</c>, which is a deployment-wide kill switch: with
    /// that off, nothing is checked whatever this says. Nullable rather than defaulted so existing rows
    /// need no backfill.
    /// </para>
    /// </summary>
    public bool? FreshnessChecksEnabled { get; set; }

    /// <summary>
    /// Who is emailed when one of this company's pipeline steps goes stale — a <c>;</c>-separated list, the
    /// same separator metric filters use. Null or empty falls back to
    /// <c>Pipelines:FreshnessAlertRecipients</c>, and if that is empty too the sweep still records verdicts
    /// and the API still serves them; it just sends no mail.
    /// </summary>
    public string? FreshnessAlertRecipients { get; set; }
}

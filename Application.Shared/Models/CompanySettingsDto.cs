namespace Application.Shared.Models;

/// <summary>Wire shape for the per-company settings API (shared by the server controller and the client).</summary>
public class CompanySettingsDto
{
    public bool DebugLoggingEnabled { get; set; }

    /// <summary>
    /// Date pattern for CSV exports, one of <see cref="ExportDateFormats.Allowed"/>. On a PUT, null means
    /// "leave unchanged" so a caller that only means to toggle debug logging can't blank it.
    /// </summary>
    public string? ExportDateFormat { get; set; }

    /// <summary>
    /// Whether this company's pipelines are checked against their freshness policies. On a PUT, null means
    /// "leave unchanged" — following <see cref="ExportDateFormat"/> rather than
    /// <see cref="DebugLoggingEnabled"/>, which is applied on every PUT and is a trap the client has to
    /// work around.
    /// </summary>
    public bool? FreshnessChecksEnabled { get; set; }

    /// <summary>
    /// <c>;</c>-separated recipients for stale-step alerts. On a PUT, null means "leave unchanged"; an
    /// empty string clears the list, so there is still a way to say "nobody".
    /// </summary>
    public string? FreshnessAlertRecipients { get; set; }
}

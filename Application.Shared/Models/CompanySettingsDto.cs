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

    /// <summary>
    /// Folder pipeline runs stage their files in. On a PUT, null means "leave unchanged"; an empty string
    /// clears it back to the OS temp folder, so there is still a way to say "use the default".
    /// </summary>
    public string? PipelineWorkingDirectory { get; set; }

    /// <summary>
    /// The folder that is actually in use, with the fallback already applied. Read-only: the server fills
    /// it on every response and ignores it on a PUT.
    /// <para>
    /// Sent because the browser cannot work it out. The fallback is the <em>server's</em> temp folder, and
    /// "where is my data being staged right now?" is the whole reason somebody opens this setting — a UI
    /// that could only say "the default" would not answer it.
    /// </para>
    /// </summary>
    public string? PipelineWorkingDirectoryEffective { get; set; }
}

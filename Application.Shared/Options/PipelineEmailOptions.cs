namespace Application.Shared.Options;

/// <summary>
/// Configuration for pipeline export emails, bound from the <c>PipelineEmail</c> appsettings section. Sends
/// through the same Next.js/Resend service as every other email in this solution.
/// </summary>
public class PipelineEmailOptions
{
    /// <summary>Base URI of the Next.js email service. Unset means the email destination is unavailable.</summary>
    public string? ApiBaseUri { get; set; }

    /// <summary>Route that renders and sends a pipeline export.</summary>
    public string Endpoint { get; set; } = "/api/email/pipeline-export";

    /// <summary>From address. Must be a domain Resend is verified to send for.</summary>
    public string? From { get; set; }

    /// <summary>
    /// Public base URL of the app, used to build the dataset link when an export is too large to attach.
    /// Without it the oversize fallback can still write the rows but cannot link to them.
    /// </summary>
    public string? AppBaseUri { get; set; }

    /// <summary>
    /// Largest attachment this deployment will send, in megabytes.
    /// <para>
    /// The default is deliberately well under Resend's own ceiling. Two multipliers sit between this number
    /// and the limit that actually applies: the file is base64-encoded (+33%) to travel as JSON, and the
    /// assembled MIME message is what the provider measures. A 25 MB file is already a ~34 MB request body,
    /// and mailbox providers commonly bounce above 25 MB regardless of what the sender accepts.
    /// </para>
    /// </summary>
    public int MaxAttachmentMb { get; set; } = 20;

    /// <summary>
    /// Row ceiling for a single export, independent of the byte cap. An XLSX is assembled in memory, and
    /// Excel itself stops at 1,048,576 rows per sheet — a workbook above that cannot be opened at all.
    /// </summary>
    public int MaxRows { get; set; } = 500_000;

    /// <summary>How long to wait on the email service. An attachment upload is not a fast request.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiBaseUri) && !string.IsNullOrWhiteSpace(From);

    /// <summary>The byte cap. Clamped so a mis-set 0 or a wild value cannot disable or defeat the guard.</summary>
    public long ResolveMaxAttachmentBytes() =>
        Math.Clamp(MaxAttachmentMb, 1, 40) * 1024L * 1024L;

    /// <summary>Clamped to Excel's own per-sheet row limit — a bigger workbook will not open.</summary>
    public int ResolveMaxRows() => Math.Clamp(MaxRows, 1, 1_048_575);

    public int ResolveTimeoutSeconds() => Math.Clamp(TimeoutSeconds, 5, 900);
}

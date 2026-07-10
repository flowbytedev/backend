namespace Application.Shared.Options;

/// <summary>
/// Configuration for "a table you have access to was moved" notification emails. Bound from the
/// "TableMovedEmail" appsettings section. Sends through the same Next.js/Resend email service as the
/// incident, sales-snapshot, dataset-shared and comment-mention emails.
/// </summary>
public class TableMovedEmailOptions
{
    /// <summary>Base URI of the Next.js email service.</summary>
    public string? ApiBaseUri { get; set; }

    /// <summary>Route on the email service that renders/sends the table-moved email.</summary>
    public string Endpoint { get; set; } = "/api/email/table-moved";

    /// <summary>From address used for the email.</summary>
    public string? From { get; set; }

    /// <summary>Public base URL of the app, used to build a link back to the table in its new dataset.</summary>
    public string? AppBaseUri { get; set; }
}

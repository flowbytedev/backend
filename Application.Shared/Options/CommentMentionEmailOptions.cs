namespace Application.Shared.Options;

/// <summary>
/// Configuration for "you were mentioned in a comment" notification emails. Bound from the
/// "CommentMentionEmail" appsettings section. Sends through the same Next.js/Resend email service
/// as the incident, sales-snapshot and dataset-shared emails.
/// </summary>
public class CommentMentionEmailOptions
{
    /// <summary>Base URI of the Next.js email service.</summary>
    public string? ApiBaseUri { get; set; }

    /// <summary>Route on the email service that renders/sends the mention email.</summary>
    public string Endpoint { get; set; } = "/api/email/comment-mention";

    /// <summary>From address used for the email.</summary>
    public string? From { get; set; }

    /// <summary>Public base URL of the app, used to build a link back to the commented table.</summary>
    public string? AppBaseUri { get; set; }
}

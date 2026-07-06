namespace Application.Shared.Services.Data;

public interface ICommentMentionNotificationService
{
    /// <summary>
    /// Sends a "you were mentioned in a comment" email. Never throws — a notification failure must
    /// not break comment creation.
    /// </summary>
    Task SendMentionNotificationAsync(
        string recipientEmail,
        string? recipientName,
        string mentionedByName,
        string datasetId,
        string datasetName,
        string tableName,
        string commentContent,
        string companyId);
}

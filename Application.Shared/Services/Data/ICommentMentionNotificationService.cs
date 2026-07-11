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

    /// <summary>Same "you were mentioned" email, for a notebook cell's comment thread instead of a
    /// dataset table's. Never throws.</summary>
    Task SendNotebookMentionNotificationAsync(
        string recipientEmail,
        string? recipientName,
        string mentionedByName,
        string notebookId,
        string notebookName,
        string cellName,
        string commentContent,
        string companyId);
}

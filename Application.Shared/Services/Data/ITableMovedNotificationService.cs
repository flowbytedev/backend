namespace Application.Shared.Services.Data;

public interface ITableMovedNotificationService
{
    /// <summary>
    /// Sends a "this table was moved to another dataset" email. Never throws — a notification failure
    /// must not break the move.
    /// </summary>
    Task SendTableMovedNotificationAsync(
        string recipientEmail,
        string? recipientName,
        string movedByName,
        string tableName,
        string oldDatasetName,
        string newDatasetId,
        string newDatasetName,
        string companyId);
}

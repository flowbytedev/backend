using Application.Shared.Models;
using Application.Shared.Models.Notebooks;

namespace Application.Shared.Services.Data;

public interface IEmailNotificationService
{
    Task SendDatasetSharedNotificationAsync(string recipientEmail, string datasetId, string datasetName, string companyId, string sharedByUserName, DatasetUserType userType, IReadOnlyCollection<string>? tables = null);

    Task SendNotebookSharedNotificationAsync(string recipientEmail, string notebookId, string notebookName, string companyId, string sharedByUserName, NotebookUserType userType);
}

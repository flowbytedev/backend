using Application.Helpers;
using Application.Shared.Models;

namespace Application.Services.Data;

public class EmailNotificationService : Application.Shared.Services.Data.IEmailNotificationService
{
    private readonly EmailHelper _emailHelper;

    public EmailNotificationService(EmailHelper emailHelper)
    {
        _emailHelper = emailHelper;
    }

    public async Task SendDatasetSharedNotificationAsync(string recipientEmail, string datasetName, string sharedByUserName, DatasetUserType userType)
    {
        try
        {
            // For now, this is a mock implementation
            // In a real application, you would use the EmailHelper to send actual emails
            
            var subject = $"Dataset '{datasetName}' has been shared with you";
            var body = GenerateDatasetSharedEmailBody(datasetName, sharedByUserName, userType);
            
            // Uncomment the line below to actually send emails when EmailHelper is configured
            // _emailHelper.SendEmail(recipientEmail, subject, body);
            
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending dataset shared notification: {ex.Message}");
        }
    }

    private string GenerateDatasetSharedEmailBody(string datasetName, string sharedByUserName, DatasetUserType userType)
    {
        var accessLevel = userType switch
        {
            DatasetUserType.Admin => "Administrator",
            DatasetUserType.Editor => "Editor",
            DatasetUserType.Viewer => "Viewer",
            _ => "Unknown"
        };

        return $@"
            <html>
            <body>
                <h2>Dataset Shared with You</h2>
                <p>Hello,</p>
                <p><strong>{sharedByUserName}</strong> has shared the dataset <strong>'{datasetName}'</strong> with you.</p>
                <p>Your access level: <strong>{accessLevel}</strong></p>
                
                <h3>What you can do with {accessLevel} access:</h3>
                <ul>
                    {(userType == DatasetUserType.Admin ? "<li>Full administrative access</li><li>Share with other users</li>" : "")}
                    {(userType == DatasetUserType.Editor || userType == DatasetUserType.Admin ? "<li>Edit dataset structure</li><li>Add/modify tables</li>" : "")}
                    <li>View dataset contents</li>
                    <li>Query data</li>
                    <li>Generate reports</li>
                </ul>
                
                <p>You can now access this dataset in your dashboard.</p>
                
                <p>Best regards,<br/>The FlowByte Team</p>
            </body>
            </html>";
    }
}

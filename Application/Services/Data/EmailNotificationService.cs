using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Shared.Models;
using Application.Shared.Models.Notebooks;
using Application.Shared.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Services.Data;

/// <summary>
/// Sends "dataset shared" notifications through the Next.js/Resend email service (same service used
/// for incident and sales-snapshot emails) by POSTing a payload to its dataset-shared route.
/// Never throws — a notification failure must not break the share operation.
/// </summary>
public class EmailNotificationService : Application.Shared.Services.Data.IEmailNotificationService
{
    public const string HttpClientName = "DatasetSharedEmailApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DatasetSharedEmailOptions _options;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(
        IHttpClientFactory httpClientFactory,
        IOptions<DatasetSharedEmailOptions> options,
        ILogger<EmailNotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendDatasetSharedNotificationAsync(
        string recipientEmail, string datasetId, string datasetName, string companyId, string sharedByUserName, DatasetUserType userType,
        IReadOnlyCollection<string>? tables = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiBaseUri) || string.IsNullOrWhiteSpace(_options.From))
            {
                _logger.LogWarning("[DatasetShared] Skipped — email service is not configured (ApiBaseUri/From).");
                return;
            }
            if (string.IsNullOrWhiteSpace(recipientEmail))
                return;

            var accessLevel = userType switch
            {
                DatasetUserType.Admin => "Administrator",
                DatasetUserType.Editor => "Editor",
                DatasetUserType.Viewer => "Viewer",
                _ => "Viewer"
            };

            var appBase = (_options.AppBaseUri ?? string.Empty).TrimEnd('/');
            var datasetUrl = string.IsNullOrEmpty(appBase)
                ? null
                : $"{appBase}/data/tables?c={companyId}&d={datasetId}";

            // Describe the table scope: empty = all tables, otherwise the named tables.
            var scopeSentence = tables is { Count: > 0 }
                ? $" You have access to {tables.Count} table{(tables.Count == 1 ? "" : "s")}: {string.Join(", ", tables)}."
                : " You have access to all tables in this dataset.";

            var payload = new DatasetSharedEmailPayload
            {
                From = _options.From!,
                To = new List<string> { recipientEmail.Trim() },
                Subject = $"Dataset '{datasetName}' has been shared with you",
                DatasetName = datasetName,
                SharedByName = sharedByUserName,
                AccessLevel = accessLevel,
                DatasetUrl = datasetUrl,
                Message = $"{sharedByUserName} has shared the dataset \"{datasetName}\" with you. Your access level is {accessLevel}.{scopeSentence}"
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "/api/email/dataset-shared" : _options.Endpoint;

            var response = await client.PostAsJsonAsync(endpoint, payload);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("[DatasetShared] Sent share notification for dataset {DatasetId} to {Recipient}.", datasetId, recipientEmail);
        }
        catch (Exception ex)
        {
            // Notifications must never break the share operation.
            _logger.LogError(ex, "[DatasetShared] Failed to send share notification for dataset {DatasetId}.", datasetId);
        }
    }

    public async Task SendNotebookSharedNotificationAsync(
        string recipientEmail, string notebookId, string notebookName, string companyId, string sharedByUserName, NotebookUserType userType)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiBaseUri) || string.IsNullOrWhiteSpace(_options.From))
            {
                _logger.LogWarning("[NotebookShared] Skipped — email service is not configured (ApiBaseUri/From).");
                return;
            }
            if (string.IsNullOrWhiteSpace(recipientEmail))
                return;

            var accessLevel = userType == NotebookUserType.Editor ? "Editor" : "Viewer";

            var appBase = (_options.AppBaseUri ?? string.Empty).TrimEnd('/');
            var notebookUrl = string.IsNullOrEmpty(appBase)
                ? null
                : $"{appBase}/data/notebook?c={companyId}&n={notebookId}";

            var payload = new DatasetSharedEmailPayload
            {
                From = _options.From!,
                To = new List<string> { recipientEmail.Trim() },
                Subject = $"Notebook '{notebookName}' has been shared with you",
                DatasetName = notebookName,
                SharedByName = sharedByUserName,
                AccessLevel = accessLevel,
                DatasetUrl = notebookUrl,
                Message = $"{sharedByUserName} has shared the notebook \"{notebookName}\" with you. Your access level is {accessLevel}."
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "/api/email/dataset-shared" : _options.Endpoint;

            var response = await client.PostAsJsonAsync(endpoint, payload);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("[NotebookShared] Sent share notification for notebook {NotebookId} to {Recipient}.", notebookId, recipientEmail);
        }
        catch (Exception ex)
        {
            // Notifications must never break the share operation.
            _logger.LogError(ex, "[NotebookShared] Failed to send share notification for notebook {NotebookId}.", notebookId);
        }
    }

    private sealed class DatasetSharedEmailPayload
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public List<string> To { get; set; } = new();

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("recipientName")]
        public string? RecipientName { get; set; }

        [JsonPropertyName("datasetName")]
        public string DatasetName { get; set; } = string.Empty;

        [JsonPropertyName("sharedByName")]
        public string SharedByName { get; set; } = string.Empty;

        [JsonPropertyName("accessLevel")]
        public string AccessLevel { get; set; } = string.Empty;

        [JsonPropertyName("datasetUrl")]
        public string? DatasetUrl { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

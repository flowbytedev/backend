using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Shared.Options;
using Application.Shared.Services.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Services.Data;

/// <summary>
/// Sends "this table was moved to another dataset" notifications through the Next.js/Resend email
/// service (same service used for incident, sales-snapshot, dataset-shared and comment-mention emails)
/// by POSTing a payload to its table-moved route. Never throws — a notification failure must not
/// break the move.
/// </summary>
public class TableMovedNotificationService : ITableMovedNotificationService
{
    public const string HttpClientName = "TableMovedEmailApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TableMovedEmailOptions _options;
    private readonly ILogger<TableMovedNotificationService> _logger;

    public TableMovedNotificationService(
        IHttpClientFactory httpClientFactory,
        IOptions<TableMovedEmailOptions> options,
        ILogger<TableMovedNotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendTableMovedNotificationAsync(
        string recipientEmail,
        string? recipientName,
        string movedByName,
        string tableName,
        string oldDatasetName,
        string newDatasetId,
        string newDatasetName,
        string companyId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiBaseUri) || string.IsNullOrWhiteSpace(_options.From))
            {
                _logger.LogWarning(
                    "[TableMoved] Skipped — email service is not configured. ApiBaseUri='{ApiBaseUri}', From='{From}'. Set the TableMovedEmail section in appsettings.",
                    _options.ApiBaseUri, _options.From);
                return;
            }
            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogWarning("[TableMoved] Skipped — recipient email was empty.");
                return;
            }

            var appBase = (_options.AppBaseUri ?? string.Empty).TrimEnd('/');
            var newDatasetUrl = string.IsNullOrEmpty(appBase)
                ? null
                : $"{appBase}/data/tables?c={companyId}&d={newDatasetId}";

            var payload = new TableMovedEmailPayload
            {
                From = _options.From!,
                To = new List<string> { recipientEmail.Trim() },
                Subject = $"\"{tableName}\" was moved to {newDatasetName}",
                RecipientName = recipientName,
                MovedByName = movedByName,
                TableName = tableName,
                OldDatasetName = oldDatasetName,
                NewDatasetName = newDatasetName,
                NewDatasetUrl = newDatasetUrl
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "/api/email/table-moved" : _options.Endpoint;

            _logger.LogInformation(
                "[TableMoved] POSTing table-moved email to {BaseAddress}{Endpoint} for {Recipient} (table {TableName} -> dataset {NewDatasetId}).",
                client.BaseAddress, endpoint, recipientEmail, tableName, newDatasetId);

            var response = await client.PostAsJsonAsync(endpoint, payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "[TableMoved] Email service returned {StatusCode} for table {TableName} to {Recipient}. Body: {Body}",
                    (int)response.StatusCode, tableName, recipientEmail, body);
                return;
            }

            _logger.LogInformation("[TableMoved] Sent table-moved notification for table {TableName} to {Recipient}.", tableName, recipientEmail);
        }
        catch (Exception ex)
        {
            // Notifications must never break the move.
            _logger.LogError(ex, "[TableMoved] Failed to send table-moved notification for table {TableName} to {Recipient}.", tableName, recipientEmail);
        }
    }

    private sealed class TableMovedEmailPayload
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public List<string> To { get; set; } = new();

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("recipientName")]
        public string? RecipientName { get; set; }

        [JsonPropertyName("movedByName")]
        public string MovedByName { get; set; } = string.Empty;

        [JsonPropertyName("tableName")]
        public string TableName { get; set; } = string.Empty;

        [JsonPropertyName("oldDatasetName")]
        public string OldDatasetName { get; set; } = string.Empty;

        [JsonPropertyName("newDatasetName")]
        public string NewDatasetName { get; set; } = string.Empty;

        [JsonPropertyName("newDatasetUrl")]
        public string? NewDatasetUrl { get; set; }
    }
}

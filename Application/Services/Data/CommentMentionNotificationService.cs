using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Shared.Options;
using Application.Shared.Services.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Services.Data;

/// <summary>
/// Sends "you were mentioned in a comment" notifications through the Next.js/Resend email service
/// (same service used for incident, sales-snapshot and dataset-shared emails) by POSTing a payload
/// to its comment-mention route. Never throws — a notification failure must not break commenting.
/// </summary>
public class CommentMentionNotificationService : ICommentMentionNotificationService
{
    public const string HttpClientName = "CommentMentionEmailApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CommentMentionEmailOptions _options;
    private readonly ILogger<CommentMentionNotificationService> _logger;

    public CommentMentionNotificationService(
        IHttpClientFactory httpClientFactory,
        IOptions<CommentMentionEmailOptions> options,
        ILogger<CommentMentionNotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendMentionNotificationAsync(
        string recipientEmail,
        string? recipientName,
        string mentionedByName,
        string datasetId,
        string datasetName,
        string tableName,
        string commentContent,
        string companyId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiBaseUri) || string.IsNullOrWhiteSpace(_options.From))
            {
                _logger.LogWarning(
                    "[CommentMention] Skipped — email service is not configured. ApiBaseUri='{ApiBaseUri}', From='{From}'. Set the CommentMentionEmail section in appsettings.",
                    _options.ApiBaseUri, _options.From);
                return;
            }
            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogWarning("[CommentMention] Skipped — recipient email was empty.");
                return;
            }

            var appBase = (_options.AppBaseUri ?? string.Empty).TrimEnd('/');
            var commentUrl = string.IsNullOrEmpty(appBase)
                ? null
                : $"{appBase}/data/tables?c={companyId}&d={datasetId}";

            var payload = new CommentMentionEmailPayload
            {
                From = _options.From!,
                To = new List<string> { recipientEmail.Trim() },
                Subject = $"{mentionedByName} mentioned you in a comment",
                RecipientName = recipientName,
                MentionedByName = mentionedByName,
                DatasetName = datasetName,
                TableName = tableName,
                CommentContent = commentContent,
                CommentUrl = commentUrl
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "/api/email/comment-mention" : _options.Endpoint;

            _logger.LogInformation(
                "[CommentMention] POSTing mention email to {BaseAddress}{Endpoint} for {Recipient} (dataset {DatasetId}).",
                client.BaseAddress, endpoint, recipientEmail, datasetId);

            var response = await client.PostAsJsonAsync(endpoint, payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "[CommentMention] Email service returned {StatusCode} for dataset {DatasetId} to {Recipient}. Body: {Body}",
                    (int)response.StatusCode, datasetId, recipientEmail, body);
                return;
            }

            _logger.LogInformation("[CommentMention] Sent mention notification for dataset {DatasetId} to {Recipient}.", datasetId, recipientEmail);
        }
        catch (Exception ex)
        {
            // Notifications must never break comment creation.
            _logger.LogError(ex, "[CommentMention] Failed to send mention notification for dataset {DatasetId} to {Recipient}.", datasetId, recipientEmail);
        }
    }

    public async Task SendNotebookMentionNotificationAsync(
        string recipientEmail,
        string? recipientName,
        string mentionedByName,
        string notebookId,
        string notebookName,
        string cellName,
        string commentContent,
        string companyId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiBaseUri) || string.IsNullOrWhiteSpace(_options.From))
            {
                _logger.LogWarning("[CommentMention] Skipped notebook mention — email service is not configured.");
                return;
            }
            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                _logger.LogWarning("[CommentMention] Skipped notebook mention — recipient email was empty.");
                return;
            }

            var appBase = (_options.AppBaseUri ?? string.Empty).TrimEnd('/');
            var commentUrl = string.IsNullOrEmpty(appBase)
                ? null
                : $"{appBase}/data/notebook?c={companyId}&n={notebookId}";

            var payload = new CommentMentionEmailPayload
            {
                From = _options.From!,
                To = new List<string> { recipientEmail.Trim() },
                Subject = $"{mentionedByName} mentioned you in a comment",
                RecipientName = recipientName,
                MentionedByName = mentionedByName,
                DatasetName = notebookName,
                TableName = cellName,
                CommentContent = commentContent,
                CommentUrl = commentUrl
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "/api/email/comment-mention" : _options.Endpoint;

            var response = await client.PostAsJsonAsync(endpoint, payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "[CommentMention] Email service returned {StatusCode} for notebook {NotebookId} to {Recipient}. Body: {Body}",
                    (int)response.StatusCode, notebookId, recipientEmail, body);
                return;
            }

            _logger.LogInformation("[CommentMention] Sent notebook mention notification for notebook {NotebookId} to {Recipient}.", notebookId, recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CommentMention] Failed to send notebook mention notification for notebook {NotebookId} to {Recipient}.", notebookId, recipientEmail);
        }
    }

    private sealed class CommentMentionEmailPayload
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public List<string> To { get; set; } = new();

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("recipientName")]
        public string? RecipientName { get; set; }

        [JsonPropertyName("mentionedByName")]
        public string MentionedByName { get; set; } = string.Empty;

        [JsonPropertyName("datasetName")]
        public string DatasetName { get; set; } = string.Empty;

        [JsonPropertyName("tableName")]
        public string TableName { get; set; } = string.Empty;

        [JsonPropertyName("commentContent")]
        public string CommentContent { get; set; } = string.Empty;

        [JsonPropertyName("commentUrl")]
        public string? CommentUrl { get; set; }
    }
}

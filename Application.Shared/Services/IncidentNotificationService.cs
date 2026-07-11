using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Shared.Services;

public class IncidentNotificationService : IIncidentNotificationService
{
    private const int MaxUpstreamDepth = 10;
    public const string HttpClientName = "IncidentEmailApi";

    private readonly StatusDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IncidentEmailOptions _options;
    private readonly ILogger<IncidentNotificationService> _logger;

    public IncidentNotificationService(
        StatusDbContext context,
        IHttpClientFactory httpClientFactory,
        IOptions<IncidentEmailOptions> options,
        ILogger<IncidentNotificationService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task NotifyIncidentOpenedAsync(Incident incident, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiBaseUri) || string.IsNullOrWhiteSpace(_options.From))
            {
                _logger.LogWarning("[IncidentNotification] Skipped — email service is not configured.");
                return;
            }

            var targetEntityIds = await ResolveTargetEntityIdsAsync(incident.EntityId, ct);

            var audience = await _context.EntityAudiences
                .Where(a => targetEntityIds.Contains(a.EntityId)
                            && a.IsActive
                            && a.CompanyId == incident.CompanyId
                            && a.Email != null && a.Email != "")
                .ToListAsync(ct);

            // Dedupe recipients by email (a user may be on the entity and an upstream entity).
            var recipients = audience
                .Select(a => a.Email.Trim())
                .Where(e => e.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (recipients.Count == 0)
            {
                _logger.LogInformation("[IncidentNotification] No audience to notify for incident {IncidentId}.", incident.Id);
                return;
            }

            var entityName = incident.Entity?.Name
                ?? (await _context.MonitoredAssets
                        .Where(e => e.Id == incident.EntityId)
                        .Select(e => e.Name)
                        .FirstOrDefaultAsync(ct))
                ?? "the affected service";

            var appBase = (_options.AppBaseUri ?? string.Empty).TrimEnd('/');
            var statusUrl = $"{appBase}/status/incidents/{incident.Id}?c={incident.CompanyId}";

            var payload = new IncidentEmailPayload
            {
                From = _options.From!,
                To = recipients,
                Subject = $"[{incident.Severity}] Issue detected: {entityName}",
                EntityName = entityName,
                IncidentTitle = incident.Title,
                Severity = incident.Severity.ToString(),
                Message = $"An issue has been detected with {entityName}. The team has been notified and is working on it.",
                StatusUrl = statusUrl
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "/api/email/incident" : _options.Endpoint;

            var response = await client.PostAsJsonAsync(endpoint, payload, ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "[IncidentNotification] Sent incident {IncidentId} email to {Count} recipient(s).",
                incident.Id, recipients.Count);
        }
        catch (Exception ex)
        {
            // Notifications must never break incident creation.
            _logger.LogError(ex, "[IncidentNotification] Failed to send email for incident {IncidentId}.", incident.Id);
        }
    }

    public async Task NotifyGenericAsync(
        IEnumerable<string> recipientEmails,
        string subject,
        string entityName,
        string incidentTitle,
        string? severity,
        string? message,
        string statusUrl,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiBaseUri) || string.IsNullOrWhiteSpace(_options.From))
            {
                _logger.LogWarning("[IncidentNotification] Skipped generic notification — email service is not configured.");
                return;
            }

            var recipients = recipientEmails
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0) return;

            var payload = new IncidentEmailPayload
            {
                From = _options.From!,
                To = recipients,
                Subject = subject,
                EntityName = entityName,
                IncidentTitle = incidentTitle,
                Severity = severity ?? "",
                Message = message ?? "",
                StatusUrl = statusUrl,
            };

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "/api/email/incident" : _options.Endpoint;

            var response = await client.PostAsJsonAsync(endpoint, payload, ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("[IncidentNotification] Sent generic notification '{Subject}' to {Count} recipient(s).", subject, recipients.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IncidentNotification] Failed to send generic notification '{Subject}'.", subject);
        }
    }

    /// <summary>
    /// The incident's entity plus every entity it transitively depends on (upstream), cycle-safe.
    /// </summary>
    private async Task<HashSet<string>> ResolveTargetEntityIdsAsync(string rootEntityId, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootEntityId };
        var frontier = new List<string> { rootEntityId };

        for (var depth = 0; depth < MaxUpstreamDepth && frontier.Count > 0; depth++)
        {
            var current = frontier;
            var upstream = await _context.AssetDependencies
                .Where(d => current.Contains(d.EntityId) && d.IsActive)
                .Select(d => d.DependsOnEntityId)
                .Distinct()
                .ToListAsync(ct);

            frontier = upstream.Where(id => !string.IsNullOrEmpty(id) && visited.Add(id)).ToList();
        }

        return visited;
    }

    private sealed class IncidentEmailPayload
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public List<string> To { get; set; } = new();

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("entityName")]
        public string EntityName { get; set; } = string.Empty;

        [JsonPropertyName("incidentTitle")]
        public string IncidentTitle { get; set; } = string.Empty;

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("statusUrl")]
        public string StatusUrl { get; set; } = string.Empty;
    }
}

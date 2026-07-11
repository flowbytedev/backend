using Application.Shared.Models;

namespace Application.Shared.Services;

public interface IIncidentNotificationService
{
    /// <summary>
    /// Notifies the audience of the incident's entity — and the audiences of every entity
    /// it transitively depends on (upstream) — that an incident has been opened.
    /// Never throws: email failures are logged and swallowed so incident creation is unaffected.
    /// </summary>
    Task NotifyIncidentOpenedAsync(Incident incident, CancellationToken ct = default);

    /// <summary>Reuses the same incident-notification email transport for a caller-supplied recipient list
    /// and message — e.g. a scheduled notebook run failure, which has no "entity audience" to resolve.
    /// No-ops if <paramref name="recipientEmails"/> is empty. Never throws.</summary>
    Task NotifyGenericAsync(
        IEnumerable<string> recipientEmails,
        string subject,
        string entityName,
        string incidentTitle,
        string? severity,
        string? message,
        string statusUrl,
        CancellationToken ct = default);
}

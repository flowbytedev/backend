using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Shared.Services.Data;

/// <summary>
/// Hangfire entry point for a notebook's recurring "Run All". Lives in Shared (like <see cref="IngestionJob"/>)
/// so both the web app and the scheduler can reference the same job type, though in practice only the
/// scheduler's recurring registrar (<c>NotebookRunRegistrarJob</c>) schedules it — runs execute with
/// <c>isAdmin: true</c> (the schedule itself was authorized when the owner turned it on; there's no
/// "acting user" for a cron trigger) and are tagged <c>triggeredBy: "scheduled"</c> so run history and the
/// notebook's own <c>LastScheduledRun*</c> fields distinguish them from interactive runs.
/// </summary>
public class NotebookRunJob
{
    private readonly IQueryNotebookService _notebooks;
    private readonly Services.IIncidentNotificationService _incidentNotifier;
    private readonly Options.NotebookOpsOptions _opsOptions;
    private readonly ILogger<NotebookRunJob> _logger;

    public NotebookRunJob(
        IQueryNotebookService notebooks,
        Services.IIncidentNotificationService incidentNotifier,
        IOptions<Options.NotebookOpsOptions> opsOptions,
        ILogger<NotebookRunJob> logger)
    {
        _notebooks = notebooks;
        _incidentNotifier = incidentNotifier;
        _opsOptions = opsOptions.Value;
        _logger = logger;
    }

    [Queue("notebook")]
    public async Task RunAsync(string companyId, string notebookId, string notebookName, string ownerUserId, PerformContext? context, CancellationToken ct = default)
    {
        var result = await _notebooks.RunAllAsync(companyId, ownerUserId, isAdmin: true, notebookId, parameters: null, triggeredBy: "scheduled", ct: ct);

        var failures = result.Cells.Where(c => c.Status == "error").ToList();
        if (failures.Count == 0)
        {
            _logger.LogInformation("[NotebookRun] Scheduled run for notebook {NotebookId} completed successfully ({CellCount} cell(s)).", notebookId, result.Cells.Count);
            return;
        }

        _logger.LogWarning("[NotebookRun] Scheduled run for notebook {NotebookId} had {FailureCount} failing cell(s).", notebookId, failures.Count);

        if (_opsOptions.FailureRecipients.Count == 0) return;

        var summary = string.Join("; ", failures.Select(f => $"{f.CellId}: {f.Error}"));
        await _incidentNotifier.NotifyGenericAsync(
            recipientEmails: _opsOptions.FailureRecipients,
            subject: $"Scheduled notebook run failed: {notebookName}",
            entityName: notebookName,
            incidentTitle: "Scheduled notebook run failed",
            severity: "error",
            message: $"A scheduled Run All failed for {failures.Count} of {result.Cells.Count} cell(s). {summary}",
            statusUrl: $"/data/notebook?c={companyId}&n={notebookId}",
            ct: ct);
    }
}

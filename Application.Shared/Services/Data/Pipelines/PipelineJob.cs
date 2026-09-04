using Application.Shared.Models;
using Application.Shared.Models.Data.Pipelines;
using Application.Shared.Options;
using Hangfire.Server;
using Microsoft.Extensions.Options;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Hangfire entry point for a pipeline run. Lives in Shared so both the web app (which enqueues) and the
/// scheduler (which enqueues recurring runs and executes everything) reference one job type. It owns the
/// <see cref="PerformContext"/>, turns it into an <see cref="IJobProgress"/>, and delegates to
/// <see cref="IPipelineEngine"/> — which stays Hangfire-free.
/// <para>
/// Mirrors <c>IngestionJob</c> exactly, including the two-argument shape: a run id supplied by the web app
/// means "execute this row I already created"; null means "this is a scheduled fire, create the row".
/// </para>
/// </summary>
public class PipelineJob(IPipelineEngine engine, IPipelineService pipelines)
{
    /// <param name="runId">
    /// An existing Queued run to execute. Null for a cron fire, where no row exists yet.
    /// </param>
    public async Task RunAsync(
        string pipelineId, string companyId, string? runId, PerformContext? context,
        CancellationToken ct = default)
    {
        var progress = context is not null ? new HangfireJobProgress(context) : null;

        if (string.IsNullOrWhiteSpace(runId))
        {
            var created = await pipelines.CreateQueuedRunAsync(
                companyId, pipelineId, PipelineTriggerType.Cron, "schedule", null, ct: ct);

            if (!created.Success)
            {
                // Written to the Hangfire console rather than thrown: a disabled or invalid pipeline is not
                // a job failure to retry, it is a state the operator has to change.
                progress?.WriteLine($"Not started: {created.Error}");
                return;
            }

            runId = created.RunId;
        }

        var jobId = context?.BackgroundJob?.Id;
        if (jobId is not null && runId is not null)
            await pipelines.SetRunJobIdAsync(runId, jobId, ct);

        await engine.RunAsync(runId!, progress, ct);
    }
}

/// <summary>
/// Periodic housekeeping: abandon runs whose runner died, hard-delete step rows past their retention, and
/// evaluate freshness policies. Separate from the registrar because these touch runs rather than schedules.
/// <para>
/// Freshness lives here rather than in its own recurring job because it needs the same cadence and has the
/// same failure mode — a sweep that stops sweeping. One job to notice is better than two.
/// </para>
/// </summary>
public class PipelineMaintenanceJob(
    IPipelineService pipelines,
    IPipelineFreshnessService freshness,
    ICompanySettingsService companySettings,
    PipelineOptions options,
    IIncidentNotificationService notifications,
    IOptions<PipelineEmailOptions> email)
{
    public async Task RunAsync(PerformContext? context, CancellationToken ct = default)
    {
        var progress = context is not null ? new HangfireJobProgress(context) : null;

        var stale = await pipelines.FailStaleRunsAsync(
            TimeSpan.FromMinutes(Math.Max(5, options.StaleRunMinutes)), ct);
        if (stale > 0) progress?.WriteLine($"Abandoned {stale} run(s) with no runner.");

        // Ordered after the abandon sweep and before the purge, deliberately. A run abandoned above has
        // just had its steps left un-succeeded, which is exactly the state freshness should notice; and
        // running before the purge means the seeding path can still see step history on its first pass.
        if (options.FreshnessChecksEnabled)
            await CheckFreshnessAsync(progress, ct);

        var purged = await pipelines.PurgeOldStepsAsync(options.StepRetentionDays, ct);
        if (purged > 0) progress?.WriteLine($"Removed {purged} step row(s) past retention.");
    }

    private async Task CheckFreshnessAsync(HangfireJobProgress? progress, CancellationToken ct)
    {
        var reports = await freshness.SweepAsync(ct);

        foreach (var report in reports)
        {
            var changed = await freshness.CommitAlertStateAsync(report, ct);
            if (changed.Count == 0) continue;

            // Only the independent causes are announced. A destination that is late because its source is
            // late does not need its own line, and on a wide graph it would bury the one that matters.
            var causes = changed.Where(c => c.IsRootCause).ToList();
            var recovered = changed
                .Where(c => c.Status == PipelineFreshnessStatus.Fresh)
                .ToList();

            foreach (var cause in causes)
                progress?.WriteLine(
                    $"{report.PipelineName}: {cause.Label ?? cause.NodeId} is {cause.Status} — {cause.Reason}");

            foreach (var back in recovered)
                progress?.WriteLine(
                    $"{report.PipelineName}: {back.Label ?? back.NodeId} is fresh again — {back.Reason}");

            if (causes.Count > 0)
                await NotifyAsync(report, causes, recovered: false, ct);
            else if (recovered.Count > 0)
                await NotifyAsync(report, recovered, recovered: true, ct);
        }
    }

    private async Task NotifyAsync(
        PipelineFreshnessReport report, List<PipelineFreshnessVerdict> nodes, bool recovered,
        CancellationToken ct)
    {
        // The company's own list wins; the appsettings one is the fallback for a deployment that has not
        // set them per company. Empty means send nothing — the verdicts are already recorded either way,
        // so silence here loses no state.
        var companyRecipients = await companySettings.GetFreshnessAsync(
            report.CompanyId ?? string.Empty, ct);

        var recipients = AlertRecipients.Resolve(
            companyRecipients.Recipients, options.FreshnessAlertRecipients);

        if (recipients.Count == 0) return;

        var subject = recovered
            ? $"Recovered: {report.PipelineName} is fresh again"
            : $"Stale data: {report.PipelineName}";

        var body = string.Join(
            "\n",
            nodes.Select(n => $"- {n.Label ?? n.NodeId} ({n.NodeType}): {n.Reason}"));

        var appBaseUri = email.Value.AppBaseUri;
        var url = string.IsNullOrWhiteSpace(appBaseUri)
            ? string.Empty
            : $"{appBaseUri!.TrimEnd('/')}/data/pipelines/{report.PipelineId}";

        // Never throws by contract, so a mail outage cannot stop the sweep from recording verdicts —
        // which is the part that must not be lost, since the alert fires on the transition.
        await notifications.NotifyGenericAsync(
            recipients,
            subject,
            report.PipelineName ?? report.PipelineId,
            recovered ? "Freshness recovered" : "Freshness policy breached",
            recovered ? "Info" : "High",
            $"{report.Summary}\n\n{body}",
            url,
            ct);
    }
}

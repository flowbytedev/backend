using Application.Shared.Models.Data.Pipelines;
using Hangfire.Server;

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
/// Periodic housekeeping: abandon runs whose runner died, and hard-delete step rows past their retention.
/// Separate from the registrar because these touch runs rather than schedules.
/// </summary>
public class PipelineMaintenanceJob(IPipelineService pipelines, PipelineOptions options)
{
    public async Task RunAsync(PerformContext? context, CancellationToken ct = default)
    {
        var progress = context is not null ? new HangfireJobProgress(context) : null;

        var stale = await pipelines.FailStaleRunsAsync(
            TimeSpan.FromMinutes(Math.Max(5, options.StaleRunMinutes)), ct);
        if (stale > 0) progress?.WriteLine($"Abandoned {stale} run(s) with no runner.");

        var purged = await pipelines.PurgeOldStepsAsync(options.StepRetentionDays, ct);
        if (purged > 0) progress?.WriteLine($"Removed {purged} step row(s) past retention.");
    }
}

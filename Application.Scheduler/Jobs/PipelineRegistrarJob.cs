using Application.Shared.Data;
using Application.Shared.Models;
using Hangfire;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Scheduler.Jobs;

/// <summary>
/// Reconciles Hangfire recurring jobs against the <c>pipeline</c> table, so a schedule created, edited,
/// paused or deleted in the web UI takes effect without restarting the scheduler. Runs on a short recurring
/// schedule and once at startup.
/// <para>
/// A direct counterpart to <see cref="IngestionRegistrarJob"/>. The database is the source of truth: if
/// Hangfire's own storage were rebuilt, the next pass would restore every schedule from these rows.
/// </para>
/// </summary>
public class PipelineRegistrarJob(ApplicationDbContext db, ILogger<PipelineRegistrarJob> logger)
{
    private const string JobPrefix = "pipeline-";

    public async Task RunAsync(PerformContext? context, CancellationToken ct = default)
    {
        var pipelines = await db.Pipeline.AsNoTracking()
            .Select(p => new { p.Id, p.CompanyId, p.Name, p.CronExpression, p.TimeZone, p.IsEnabled, p.Valid })
            .ToListAsync(ct);

        var liveJobIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pipeline in pipelines)
        {
            var jobId = JobPrefix + pipeline.Id;

            // No cron means manual-only, which is a normal state rather than a problem. A disabled or
            // invalid pipeline is unscheduled rather than left to fail every night.
            if (!pipeline.IsEnabled || !pipeline.Valid || string.IsNullOrWhiteSpace(pipeline.CronExpression))
            {
                RecurringJob.RemoveIfExists(jobId);
                continue;
            }

            var tz = ResolveTimeZone(pipeline.TimeZone, pipeline.Id, pipeline.Name);
            try
            {
                // Queue pinned explicitly: in Hangfire 1.8 a [Queue] attribute alone is not honoured for
                // recurring jobs. It must be "default" — the sales box has no access to the DuckDB files.
                RecurringJob.AddOrUpdate<Application.Shared.Services.Data.Pipelines.PipelineJob>(
                    recurringJobId: jobId,
                    queue: "default",
                    methodCall: job => job.RunAsync(pipeline.Id, pipeline.CompanyId, null, null, CancellationToken.None),
                    cronExpression: pipeline.CronExpression,
                    timeZone: tz);

                liveJobIds.Add(jobId);
            }
            catch (Exception ex)
            {
                // One bad cron must not abort the whole reconcile pass and leave every other schedule stale.
                logger.LogWarning(ex, "Could not schedule pipeline {PipelineId} '{Name}' (cron '{Cron}').",
                    pipeline.Id, pipeline.Name, pipeline.CronExpression);
            }
        }

        // Hangfire does not know about deleted rows, so anything of ours it still holds is removed here.
        using var connection = JobStorage.Current.GetConnection();
        foreach (var recurring in connection.GetRecurringJobs())
        {
            if (RegistrarSweep.IsOwned(recurring.Id, JobPrefix) && !liveJobIds.Contains(recurring.Id))
                RecurringJob.RemoveIfExists(recurring.Id);
        }
    }

    /// <summary>
    /// The zone this pipeline's cron is read in. Falling back is right for a pipeline that names no zone,
    /// and is the least-bad option for one whose zone this box cannot resolve — but the second case is a
    /// pipeline running at an hour nobody asked for, so it is said out loud rather than swallowed.
    /// </summary>
    private TimeZoneInfo? ResolveTimeZone(string? id, string pipelineId, string name)
    {
        if (string.IsNullOrWhiteSpace(id)) return ScheduleTimeZones.Default;

        var resolved = ScheduleTimeZones.Resolve(id);
        if (resolved is not null) return resolved;

        logger.LogWarning(
            "Pipeline {PipelineId} '{Name}' asks for time zone '{TimeZone}', which this host cannot resolve. "
            + "Its schedule will run on {Fallback} instead.",
            pipelineId, name, id, ScheduleTimeZones.DefaultId);

        return ScheduleTimeZones.Default;
    }
}

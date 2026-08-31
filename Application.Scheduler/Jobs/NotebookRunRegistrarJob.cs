using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Services.Data;
using Hangfire;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Scheduler.Jobs;

/// <summary>
/// Reconciles Hangfire recurring jobs against <c>query_notebook</c>'s schedule columns so that schedules
/// created, edited, disabled or deleted in the web UI take effect without restarting the scheduler.
/// Mirrors <see cref="IngestionRegistrarJob"/>. Runs on a short recurring schedule (and once at startup).
/// </summary>
public class NotebookRunRegistrarJob
{
    private const string JobPrefix = "notebook-run-";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<NotebookRunRegistrarJob> _logger;

    public NotebookRunRegistrarJob(ApplicationDbContext db, ILogger<NotebookRunRegistrarJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RunAsync(PerformContext? context, CancellationToken ct = default)
    {
        var notebooks = await _db.QueryNotebook.AsNoTracking().ToListAsync(ct);
        var liveJobIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var notebook in notebooks)
        {
            var jobId = JobPrefix + notebook.Id;

            if (!notebook.ScheduleEnabled || string.IsNullOrWhiteSpace(notebook.CronExpression) || string.IsNullOrWhiteSpace(notebook.CreatedBy))
            {
                RecurringJob.RemoveIfExists(jobId);
                continue;
            }

            var tz = ResolveTimeZone(notebook.ScheduleTimeZone);
            try
            {
                RecurringJob.AddOrUpdate<NotebookRunJob>(
                    recurringJobId: jobId,
                    methodCall: job => job.RunAsync(notebook.CompanyId, notebook.Id, notebook.Name, notebook.CreatedBy!, null, CancellationToken.None),
                    cronExpression: notebook.CronExpression,
                    timeZone: tz,
                    queue: "notebook"); // Pin the recurring definition's queue — [Queue] alone isn't enough (see SalesSnapshotEmailJob).
                liveJobIds.Add(jobId);
            }
            catch (Exception ex)
            {
                // A bad cron on one notebook shouldn't break the whole reconcile pass.
                _logger.LogWarning(ex, "Could not schedule notebook {NotebookId} (cron '{Cron}').", notebook.Id, notebook.CronExpression);
            }
        }

        // Remove recurring jobs for notebooks that were deleted (Hangfire doesn't know about deletions).
        using var connection = JobStorage.Current.GetConnection();
        foreach (var recurring in connection.GetRecurringJobs())
        {
            if (RegistrarSweep.IsOwned(recurring.Id, JobPrefix) && !liveJobIds.Contains(recurring.Id))
                RecurringJob.RemoveIfExists(recurring.Id);
        }
    }

    /// <summary>
    /// The zone this schedule's cron is read in, falling back to the default when it names none — or names
    /// one this host cannot resolve. See <see cref="ScheduleTimeZones"/> for why the id that ends up stored
    /// on the recurring job matters more than the one we looked up.
    /// </summary>
    private static TimeZoneInfo? ResolveTimeZone(string? id) =>
        ScheduleTimeZones.Resolve(id) ?? ScheduleTimeZones.Default;
}

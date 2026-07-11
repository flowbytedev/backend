using Application.Shared.Data;
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
            if (recurring.Id.StartsWith(JobPrefix, StringComparison.Ordinal) && !liveJobIds.Contains(recurring.Id))
                RecurringJob.RemoveIfExists(recurring.Id);
        }
    }

    private static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) id = "Asia/Beirut";
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Middle East Standard Time"); }
            catch { return null; }
        }
    }
}

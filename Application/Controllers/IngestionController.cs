using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Authorization;
using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Controllers;

/// <summary>
/// Manages a dataset's scheduled ingestion sources (external DB / REST / Blob / SFTP pulls) and their
/// run history. Mutations require EDIT_DATA; secrets are encrypted on write and never returned.
/// </summary>
[Route("api/datasets/{datasetId}/ingestion")]
[ApiController]
[Authorize(Policy = PolicyNames.DatasetsAccess)]
public class IngestionController : ControllerBase
{
    private readonly IIngestionService _ingestionService;
    private readonly IDatasetService _datasetService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public IngestionController(IIngestionService ingestionService, IDatasetService datasetService, IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _ingestionService = ingestionService;
        _datasetService = datasetService;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    // GET: api/ingestion  — every source across the company's datasets (the page shows these up front;
    // the dataset picker is only a filter). Absolute route so it isn't nested under a dataset id.
    [HttpGet("~/api/ingestion")]
    public async Task<ActionResult<IEnumerable<IngestionSourceDto>>> GetAllSources()
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();

        var all = await _ingestionService.GetAllSourcesAsync(companyId, HttpContext.RequestAborted);
        EnrichWithHangfire(all);
        return Ok(all);
    }

    // GET: api/datasets/{datasetId}/ingestion
    [HttpGet]
    public async Task<ActionResult<IEnumerable<IngestionSourceDto>>> GetSources(string datasetId)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        if (!await DatasetAccessible(datasetId, userId)) return NotFound($"Dataset '{datasetId}' not found.");

        var list = await _ingestionService.GetSourcesAsync(companyId, datasetId, HttpContext.RequestAborted);
        EnrichWithHangfire(list);
        return Ok(list);
    }

    // GET: api/datasets/{datasetId}/ingestion/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<IngestionSourceDto>> GetSource(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();

        var source = await _ingestionService.GetSourceAsync(companyId, id, HttpContext.RequestAborted);
        return source == null ? NotFound() : Ok(source);
    }

    // POST: api/datasets/{datasetId}/ingestion
    [HttpPost]
    public async Task<ActionResult<IngestionSourceDto>> CreateSource(string datasetId, [FromBody] SaveIngestionSourceRequest request)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TargetTable))
            return BadRequest("Name and target table are required");
        if (!await DatasetAccessible(datasetId, userId)) return NotFound($"Dataset '{datasetId}' not found.");

        return Ok(await _ingestionService.CreateAsync(companyId, datasetId, userId, request, HttpContext.RequestAborted));
    }

    // PUT: api/datasets/{datasetId}/ingestion/{id}
    [HttpPut("{id}")]
    public async Task<ActionResult<IngestionSourceDto>> UpdateSource(string datasetId, string id, [FromBody] SaveIngestionSourceRequest request)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();
        if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TargetTable))
            return BadRequest("Name and target table are required");

        var updated = await _ingestionService.UpdateAsync(companyId, id, request, HttpContext.RequestAborted);
        return updated == null ? NotFound() : Ok(updated);
    }

    // DELETE: api/datasets/{datasetId}/ingestion/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSource(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        return await _ingestionService.DeleteAsync(companyId, id, HttpContext.RequestAborted)
            ? NoContent()
            : NotFound();
    }

    // GET: api/datasets/{datasetId}/ingestion/{id}/runs
    [HttpGet("{id}/runs")]
    public async Task<ActionResult<IEnumerable<IngestionRunDto>>> GetRuns(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();

        var runs = await _ingestionService.GetRunsAsync(companyId, id, 20, HttpContext.RequestAborted);

        // Decorate Hangfire-executed runs with a dashboard deep-link when a dashboard URL is configured.
        var dashboardUrl = _configuration["Hangfire:DashboardUrl"];
        if (!string.IsNullOrWhiteSpace(dashboardUrl))
        {
            var baseUrl = dashboardUrl.TrimEnd('/');
            foreach (var run in runs)
                if (!string.IsNullOrEmpty(run.JobId))
                    run.JobUrl = $"{baseUrl}/jobs/details/{run.JobId}";
        }

        return Ok(runs);
    }

    // POST: api/datasets/{datasetId}/ingestion/{id}/run  — "Run now" (executes inline).
    [HttpPost("{id}/run")]
    public async Task<ActionResult<ImportResult>> RunNow(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        // Confirm the source belongs to this company before running it.
        var source = await _ingestionService.GetSourceAsync(companyId, id, HttpContext.RequestAborted);
        if (source == null) return NotFound();

        var result = await _ingestionService.RunSourceAsync(id, ct: HttpContext.RequestAborted);
        return Ok(result);
    }

    // POST: api/datasets/{datasetId}/ingestion/{id}/run-batch  — enqueue a Hangfire background job.
    // The job runs in the Application.Scheduler process (not inline in this request), so it survives the
    // web request ending and won't tie up a request thread for long pulls.
    [HttpPost("{id}/run-batch")]
    public async Task<ActionResult> RunBatch(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        // Confirm the source belongs to this company before queueing it.
        var source = await _ingestionService.GetSourceAsync(companyId, id, HttpContext.RequestAborted);
        if (source == null) return NotFound();

        // The Hangfire client is only registered when the SchedulerDbContext connection string is present.
        var jobClient = _serviceProvider.GetService<IBackgroundJobClient>();
        if (jobClient == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Batch execution isn't configured. Add the 'SchedulerDbContext' connection string to the web app so jobs can be queued for the scheduler.");

        // Create the run row up front (Queued) so it exists before the worker starts and shows immediately
        // in the UI. The worker reuses this exact run via its id; we then attach the Hangfire job id to it.
        var runId = await _ingestionService.CreateQueuedRunAsync(companyId, id, HttpContext.RequestAborted);

        // Enqueue the shared IngestionJob wrapper (not RunSourceAsync directly) so the worker runs with a
        // Hangfire PerformContext — that's what enables dashboard console logs + the progress bar. The
        // scheduler resolves IngestionJob and runs the same RunSourceAsync, transitioning this Queued run.
        var jobId = jobClient.Enqueue<IngestionJob>(job => job.RunAsync(id, runId, null, CancellationToken.None));
        await _ingestionService.SetRunJobIdAsync(runId, jobId, HttpContext.RequestAborted);

        return Ok(new { jobId, runId });
    }

    // POST: api/datasets/{datasetId}/ingestion/{id}/runs/reconcile
    // Marks this source's stuck "Running" runs as Failed (e.g. a "Run now" orphaned by an app restart).
    [HttpPost("{id}/runs/reconcile")]
    public async Task<ActionResult> ReconcileRuns(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        // Confirm the source belongs to this company before touching its runs.
        var source = await _ingestionService.GetSourceAsync(companyId, id, HttpContext.RequestAborted);
        if (source == null) return NotFound();

        var cleared = await _ingestionService.FailRunningRunsForSourceAsync(companyId, id, HttpContext.RequestAborted);
        return Ok(new { cleared });
    }

    // POST: api/datasets/{datasetId}/ingestion/{id}/pause  — stop running on schedule.
    [HttpPost("{id}/pause")]
    public async Task<ActionResult<IngestionSourceDto>> Pause(string datasetId, string id)
        => await SetEnabled(id, false);

    // POST: api/datasets/{datasetId}/ingestion/{id}/resume  — resume running on schedule.
    [HttpPost("{id}/resume")]
    public async Task<ActionResult<IngestionSourceDto>> Resume(string datasetId, string id)
        => await SetEnabled(id, true);

    private async Task<ActionResult<IngestionSourceDto>> SetEnabled(string id, bool enabled)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "EDIT_DATA")) return Forbid();

        var updated = await _ingestionService.SetEnabledAsync(companyId, id, enabled, HttpContext.RequestAborted);
        if (updated == null) return NotFound();

        // Reflect the change in Hangfire right away (the registrar job also reconciles periodically).
        TryReconcileRecurring(updated);
        EnrichWithHangfire(new[] { updated });
        return Ok(updated);
    }

    // ---- Hangfire recurring-job helpers (mirror Application.Scheduler's IngestionRegistrarJob) ----

    private const string RecurringPrefix = "ingest-";

    private bool HangfireConfigured => _serviceProvider.GetService<IBackgroundJobClient>() != null;

    /// <summary>Populates <see cref="IngestionSourceDto.Status"/> / <see cref="IngestionSourceDto.NextRunAt"/>
    /// from the live Hangfire recurring-job state. No-ops (leaves an enabled/disabled fallback) when
    /// Hangfire isn't configured in the web app.</summary>
    private void EnrichWithHangfire(IReadOnlyCollection<IngestionSourceDto> dtos)
    {
        if (dtos.Count == 0) return;

        Dictionary<string, RecurringJobDto>? recurring = null;
        if (HangfireConfigured)
        {
            try
            {
                using var connection = JobStorage.Current.GetConnection();
                recurring = connection.GetRecurringJobs()
                    .Where(r => r.Id.StartsWith(RecurringPrefix, StringComparison.Ordinal))
                    .ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
            }
            catch { recurring = null; }
        }

        var scheduleKnown = recurring != null;
        foreach (var d in dtos)
        {
            var scheduled = false;
            if (recurring != null && recurring.TryGetValue(RecurringPrefix + d.Id, out var rj))
            {
                scheduled = true;
                d.NextRunAt = rj.NextExecution;
            }
            d.Status = ComputeStatus(d, scheduleKnown, scheduled);
        }
    }

    private static string ComputeStatus(IngestionSourceDto d, bool scheduleKnown, bool scheduled)
    {
        if (!d.IsEnabled) return "Paused";
        if (d.LastRunStatus is "Running" or "Queued") return "Running";
        if (scheduleKnown && !scheduled) return "Pending"; // enabled but not registered yet
        if (d.LastRunStatus == "Failed") return "Error";
        return "Active";
    }

    private void TryReconcileRecurring(IngestionSourceDto s)
    {
        if (!HangfireConfigured) return;
        var jobId = RecurringPrefix + s.Id;
        try
        {
            if (!s.IsEnabled)
            {
                RecurringJob.RemoveIfExists(jobId);
                return;
            }
            RecurringJob.AddOrUpdate<Application.Shared.Services.Data.IngestionJob>(
                recurringJobId: jobId,
                methodCall: job => job.RunAsync(s.Id, null, null, CancellationToken.None),
                cronExpression: s.CronExpression,
                timeZone: ResolveTimeZone(s.TimeZone));
        }
        catch
        {
            // A bad cron or transient storage error shouldn't fail the request — the registrar reconciles.
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

    private (string companyId, string userId, string? error) ReadHeaders()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(companyId)) return ("", "", "Company ID is required");
        if (string.IsNullOrWhiteSpace(userId)) return ("", "", "User ID is required in headers");
        return (companyId, userId, null);
    }

    private async Task<bool> DatasetAccessible(string datasetId, string userId)
        => !string.IsNullOrWhiteSpace(datasetId) && await _datasetService.GetDatasetAsync(datasetId, userId) != null;
}

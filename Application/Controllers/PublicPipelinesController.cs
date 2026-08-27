using Application.Shared.Models.Data.Pipelines;
using Application.Shared.Services.Data.Pipelines;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// The API trigger: lets an external system start a pipeline with an API key. Authentication, tenancy and
/// the acting-user header all come from <see cref="PublicApiControllerBase"/>.
/// <para>
/// A pipeline must opt in via <c>ApiEnabled</c>. That flag is the whole authorization story here: the key
/// is per-company, so without it any key holder could trigger any of that company's pipelines, and
/// "triggerable from outside" is a decision the pipeline's author should make explicitly.
/// </para>
/// </summary>
[Route("api/public/pipelines")]
public class PublicPipelinesController(
    IPipelineService pipelines,
    IServiceProvider serviceProvider) : PublicApiControllerBase
{
    /// <summary>Pipelines this key may trigger. Lets a caller discover what is available without guessing ids.</summary>
    [HttpGet]
    public async Task<ActionResult> List()
    {
        if (!TryGetContext(out var companyId, out _, out var error)) return error!;

        var all = await pipelines.GetAllAsync(companyId, HttpContext.RequestAborted);

        return Ok(all
            .Where(p => p.ApiEnabled && p.IsEnabled && p.Valid)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                description = p.Description,
                last_run_at = p.LastRunAt,
                last_run_status = p.LastRunStatus
            }));
    }

    /// <summary>
    /// Queues a run and returns 202 with its id. Always asynchronous: a load can take minutes, and holding
    /// an HTTP connection open for it would tie the caller's timeout to our data volume.
    /// </summary>
    [HttpPost("{idOrName}/runs")]
    public async Task<ActionResult> Run(string idOrName, [FromBody] PipelineRunRequest? request)
    {
        if (!TryGetContext(out var companyId, out var userId, out var error)) return error!;

        var all = await pipelines.GetAllAsync(companyId, HttpContext.RequestAborted);
        var pipeline = all.FirstOrDefault(p => p.Id == idOrName)
                       ?? all.FirstOrDefault(p =>
                           string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));

        if (pipeline is null) return NotFound(new { error = $"No pipeline '{idOrName}'." });

        // Deliberately the same 404 as "does not exist": whether a pipeline exists but is not
        // API-enabled is not something an API key holder needs to be able to probe.
        if (!pipeline.ApiEnabled)
            return NotFound(new { error = $"No pipeline '{idOrName}'." });

        var jobClient = serviceProvider.GetService<IBackgroundJobClient>();
        if (jobClient is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Background execution is not configured on this server." });
        }

        var created = await pipelines.CreateQueuedRunAsync(
            companyId, pipeline.Id, PipelineTriggerType.Api, userId, request?.Params,
            ct: HttpContext.RequestAborted);

        if (!created.Success) return BadRequest(new { error = created.Error });

        var jobId = jobClient.Enqueue<PipelineJob>(
            job => job.RunAsync(pipeline.Id, companyId, created.RunId, null, CancellationToken.None));

        await pipelines.SetRunJobIdAsync(created.RunId!, jobId, HttpContext.RequestAborted);

        return Accepted(new { run_id = created.RunId, status = PipelineRunStatus.Queued });
    }

    /// <summary>Polls a run this key started, so a caller can wait for completion on its own terms.</summary>
    [HttpGet("runs/{runId}")]
    public async Task<ActionResult> GetRun(string runId)
    {
        if (!TryGetContext(out var companyId, out _, out var error)) return error!;

        var run = await pipelines.GetRunAsync(companyId, runId, HttpContext.RequestAborted);
        if (run is null) return NotFound(new { error = "No such run." });

        return Ok(new
        {
            id = run.Id,
            pipeline = run.PipelineName,
            status = run.Status,
            rows_read = run.RowsRead,
            rows_written = run.RowsWritten,
            steps_total = run.StepsTotal,
            steps_completed = run.StepsCompleted,
            steps_failed = run.StepsFailed,
            duration_ms = run.DurationMs,
            error = run.Error,
            started_at = run.StartedAt,
            finished_at = run.FinishedAt
        });
    }
}

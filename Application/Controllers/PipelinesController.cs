using Application.Shared.Authorization;
using Application.Shared.Models.Data.Pipelines;
using Application.Shared.Services.Data.Pipelines;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Manages ETL pipelines: the graph document, its YAML view, validation, previews, runs and run history.
/// <para>
/// Gated on DATA_ADMIN throughout — the same bar as scheduled ingestion, and for the same reason: a
/// pipeline writes into datasets and can read any source the service account can reach.
/// </para>
/// </summary>
[Route("api/pipelines")]
[ApiController]
[Authorize(Policy = PolicyNames.DataAdminAccess)]
public class PipelinesController(
    IPipelineService pipelines,
    IPipelineEngine engine,
    IServiceProvider serviceProvider,
    IConfiguration configuration) : ControllerBase
{
    // ------------------------------------------------------------------- CRUD

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PipelineDto>>> GetAll()
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var all = await pipelines.GetAllAsync(companyId, HttpContext.RequestAborted);
        EnrichWithHangfire(all);
        return Ok(all);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PipelineDetailDto>> Get(string id)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var pipeline = await pipelines.GetAsync(companyId, id, HttpContext.RequestAborted);
        if (pipeline is null) return NotFound();

        EnrichWithHangfire(new[] { pipeline });
        return Ok(pipeline);
    }

    [HttpPost]
    public async Task<ActionResult<PipelineDetailDto>> Create([FromBody] PipelineSaveRequest request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;

        var result = await pipelines.CreateAsync(companyId, userId, request, HttpContext.RequestAborted);
        return result.Success ? Ok(result.Pipeline) : BadRequest(result.Error);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PipelineDetailDto>> Update(string id, [FromBody] PipelineSaveRequest request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;

        var result = await pipelines.UpdateAsync(companyId, userId, id, request, HttpContext.RequestAborted);
        return result.Success ? Ok(result.Pipeline) : BadRequest(result.Error);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var deleted = await pipelines.DeleteAsync(companyId, id, HttpContext.RequestAborted);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id}/duplicate")]
    public async Task<ActionResult<PipelineDetailDto>> Duplicate(string id)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;

        var result = await pipelines.DuplicateAsync(companyId, userId, id, HttpContext.RequestAborted);
        return result.Success ? Ok(result.Pipeline) : BadRequest(result.Error);
    }

    // ------------------------------------------------------- authoring support

    /// <summary>The node catalogue. Drives the palette and every inspector form, so the UI hard-codes none of it.</summary>
    [HttpGet("node-types")]
    public ActionResult<IEnumerable<PipelineNodeSpec>> NodeTypes() => Ok(PipelineNodeCatalog.All);

    /// <summary>Validates a graph without saving it — the editor's live linter.</summary>
    [HttpPost("validate")]
    public ActionResult<PipelineValidateResponse> Validate([FromBody] PipelineValidateRequest request)
    {
        if (!TryContext(out _, out _, out var failure)) return failure!;
        return Ok(pipelines.Validate(request));
    }

    /// <summary>JSON graph to YAML. The editor's YAML tab.</summary>
    [HttpPost("to-yaml")]
    public ActionResult<PipelineYamlResponse> ToYaml([FromBody] PipelineYamlRequest request)
    {
        if (!TryContext(out _, out _, out var failure)) return failure!;

        var graph = PipelineGraph.TryParse(request.GraphJson);
        if (graph is null)
            return Ok(new PipelineYamlResponse { Error = "That pipeline is not readable." });

        return Ok(new PipelineYamlResponse { Success = true, Yaml = PipelineYaml.ToYaml(graph) });
    }

    /// <summary>
    /// YAML back to a JSON graph. <c>graphJson</c> in the request is the graph currently open, and it is
    /// what carries pinned positions and the schema cache across the edit — YAML describes neither.
    /// </summary>
    [HttpPost("from-yaml")]
    public ActionResult<PipelineYamlResponse> FromYaml([FromBody] PipelineYamlRequest request)
    {
        if (!TryContext(out _, out _, out var failure)) return failure!;

        var existing = PipelineGraph.TryParse(request.GraphJson);
        var parsed = PipelineYaml.FromYaml(request.Yaml, existing);

        return Ok(parsed.Success
            ? new PipelineYamlResponse { Success = true, GraphJson = parsed.Graph!.Serialize() }
            : new PipelineYamlResponse { Error = parsed.Error });
    }

    /// <summary>
    /// Runs the real engine over an unsaved graph, sampling sources and skipping destinations, and returns
    /// one step's output. This is what populates a mapping grid with real column names.
    /// </summary>
    [HttpPost("preview")]
    public async Task<ActionResult<PipelinePreviewResult>> Preview([FromBody] PipelinePreviewBody body)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var graph = PipelineGraph.TryParse(body.GraphJson);
        if (graph is null) return BadRequest("That pipeline is not readable.");

        var result = await engine.PreviewAsync(new PipelinePreviewRequest
        {
            Graph = graph,
            CompanyId = companyId,
            StopAfterNodeId = body.NodeId,
            RowLimit = body.Rows,
            Params = body.Params
        }, HttpContext.RequestAborted);

        return Ok(result);
    }

    /// <summary>
    /// Each step's columns as of the last run that produced any. Lets the editor show real column names
    /// without re-running a preview on every page load.
    /// </summary>
    [HttpGet("{id}/schemas")]
    public async Task<ActionResult<Dictionary<string, List<PipelineColumn>>>> Schemas(string id)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;
        return Ok(await pipelines.GetSchemasAsync(companyId, id, HttpContext.RequestAborted));
    }

    // -------------------------------------------------------------------- runs

    /// <summary>
    /// Queues a run. The row is created here (Queued) so it appears immediately, then the shared
    /// <see cref="PipelineJob"/> wrapper is enqueued — not the engine directly — so the worker runs with a
    /// Hangfire PerformContext and its console output and progress bar work.
    /// </summary>
    [HttpPost("{id}/runs")]
    public async Task<ActionResult> Run(string id, [FromBody] PipelineRunRequest? request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;

        var jobClient = serviceProvider.GetService<IBackgroundJobClient>();
        if (jobClient is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Background execution is not configured. Add the 'SchedulerDbContext' connection string to " +
                "the web app so runs can be queued for the scheduler.");
        }

        var created = await pipelines.CreateQueuedRunAsync(
            companyId, id, PipelineTriggerType.Manual, userId, request?.Params, HttpContext.RequestAborted);

        if (!created.Success) return BadRequest(created.Error);

        var jobId = jobClient.Enqueue<PipelineJob>(
            job => job.RunAsync(id, companyId, created.RunId, null, CancellationToken.None));

        await pipelines.SetRunJobIdAsync(created.RunId!, jobId, HttpContext.RequestAborted);
        return Ok(new { runId = created.RunId, jobId });
    }

    /// <summary>
    /// Forgets the incremental watermarks so the next run starts over. Destination data is untouched — which
    /// means an append destination will load everything a second time. The response says how many marks were
    /// cleared so the UI can be specific about what just happened.
    /// </summary>
    [HttpPost("{id}/state/reset")]
    public async Task<ActionResult> ResetState(string id)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var cleared = await pipelines.ResetStateAsync(companyId, id, HttpContext.RequestAborted);
        return Ok(new { cleared });
    }

    [HttpGet("{id}/runs")]
    public async Task<ActionResult<IEnumerable<PipelineRunDto>>> Runs(string id, [FromQuery] int take = 50)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var runs = await pipelines.GetRunsAsync(companyId, id, take, HttpContext.RequestAborted);
        AttachJobUrls(runs);
        return Ok(runs);
    }

    [HttpGet("runs/{runId}")]
    public async Task<ActionResult<PipelineRunDto>> GetRun(string runId)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var run = await pipelines.GetRunAsync(companyId, runId, HttpContext.RequestAborted);
        if (run is null) return NotFound();

        AttachJobUrls(new[] { run });
        return Ok(run);
    }

    [HttpGet("runs/{runId}/steps")]
    public async Task<ActionResult<IEnumerable<PipelineRunStepDto>>> Steps(string runId)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;
        return Ok(await pipelines.GetStepsAsync(companyId, runId, HttpContext.RequestAborted));
    }

    /// <summary>One step with its output preview — fetched only when a step is opened.</summary>
    [HttpGet("runs/{runId}/steps/{nodeId}")]
    public async Task<ActionResult<PipelineRunStepDto>> Step(string runId, string nodeId)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var step = await pipelines.GetStepAsync(companyId, runId, nodeId, HttpContext.RequestAborted);
        return step is null ? NotFound() : Ok(step);
    }

    /// <summary>
    /// The polling endpoint. Deliberately tiny: short field names, only steps past <paramref name="since"/>,
    /// and a revision the client can compare so an unchanged poll costs it no re-render.
    /// </summary>
    [HttpGet("runs/{runId}/status")]
    public async Task<ActionResult<PipelineRunStatusDto>> Status(string runId, [FromQuery] int since = 0)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var status = await pipelines.GetRunStatusAsync(companyId, runId, since, HttpContext.RequestAborted);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>
    /// Requests cancellation. Cooperative — the engine stops at the next step boundary, because killing a
    /// run in the middle of a write is how a table ends up half-loaded.
    /// </summary>
    [HttpPost("runs/{runId}/cancel")]
    public async Task<ActionResult> Cancel(string runId)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var cancelled = await pipelines.CancelRunAsync(companyId, runId, HttpContext.RequestAborted);
        return cancelled ? Ok() : BadRequest("That run has already finished.");
    }

    // ----------------------------------------------------------------- helpers

    private bool TryContext(out string companyId, out string userId, out ActionResult? failure)
    {
        companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? string.Empty;
        userId = Request.Headers["UserId"].ToString();

        if (string.IsNullOrWhiteSpace(companyId))
        {
            failure = BadRequest("Company ID is required");
            return false;
        }
        if (string.IsNullOrWhiteSpace(userId))
        {
            failure = BadRequest("User ID is required in headers");
            return false;
        }

        // The policy gates the route; this re-checks the specific company, since one user can belong to
        // several and the policy cannot know which one this request is for.
        if (!User.HasCompanyRole(companyId, "DATA_ADMIN"))
        {
            failure = Forbid();
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Fills in the next scheduled time from Hangfire, the way the ingestion page does. Read at request
    /// time rather than stored, because Hangfire owns that answer and a stored copy would go stale.
    /// </summary>
    private static void EnrichWithHangfire(IEnumerable<PipelineDto> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        try
        {
            using var connection = JobStorage.Current.GetConnection();
            var recurring = connection.GetRecurringJobs()
                .Where(j => j.Id.StartsWith("pipeline-", StringComparison.Ordinal))
                .ToDictionary(j => j.Id, j => j, StringComparer.Ordinal);

            foreach (var item in list)
            {
                if (recurring.TryGetValue("pipeline-" + item.Id, out var job))
                    item.NextRunAt = job.NextExecution;
                else if (item.ScheduleState == "Active")
                    // Scheduled in the database but not yet in Hangfire: the registrar reconciles every
                    // five minutes, so this is a normal transient state rather than a fault.
                    item.ScheduleState = "Pending";
            }
        }
        catch
        {
            // Hangfire storage is optional for the web app. Without it the list still works, just with no
            // next-run column — far better than failing the page.
        }
    }

    private void AttachJobUrls(IEnumerable<PipelineRunDto> runs)
    {
        var dashboard = configuration["Hangfire:DashboardUrl"];
        if (string.IsNullOrWhiteSpace(dashboard)) return;

        foreach (var run in runs.Where(r => !string.IsNullOrWhiteSpace(r.JobId)))
            run.JobUrl = $"{dashboard.TrimEnd('/')}/jobs/details/{run.JobId}";
    }
}

/// <summary>Preview body. Separate from <see cref="PipelinePreviewRequest"/> because the wire form carries
/// the graph as JSON text rather than a parsed object.</summary>
public class PipelinePreviewBody
{
    public string? GraphJson { get; set; }

    /// <summary>Which step to show. Null runs everything except destinations.</summary>
    public string? NodeId { get; set; }

    public int? Rows { get; set; }
    public Dictionary<string, string>? Params { get; set; }
}

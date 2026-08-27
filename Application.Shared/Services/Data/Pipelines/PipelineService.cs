using System.Text.Json;
using Application.Shared.Data;
using Application.Shared.Models.Data.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// CRUD and run bookkeeping for pipelines. Deliberately Hangfire-free — enqueueing is the caller's job (the
/// controller) and executing is the engine's, so this stays usable from both hosts and from a plain test.
/// </summary>
public interface IPipelineService
{
    Task<List<PipelineDto>> GetAllAsync(string companyId, CancellationToken ct = default);
    Task<PipelineDetailDto?> GetAsync(string companyId, string id, CancellationToken ct = default);
    Task<PipelineSaveResult> CreateAsync(string companyId, string? userId, PipelineSaveRequest request, CancellationToken ct = default);
    Task<PipelineSaveResult> UpdateAsync(string companyId, string? userId, string id, PipelineSaveRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(string companyId, string id, CancellationToken ct = default);

    /// <summary>
    /// Forgets every incremental watermark for a pipeline, so the next run starts from the beginning again.
    /// <para>
    /// Touches no data in any destination. That asymmetry is the thing to be loud about: with an append
    /// destination, resetting and re-running loads everything a second time on top of what is already there.
    /// Returns how many watermarks were cleared so the caller can say.
    /// </para>
    /// </summary>
    Task<int> ResetStateAsync(string companyId, string id, CancellationToken ct = default);
    Task<PipelineSaveResult> DuplicateAsync(string companyId, string? userId, string id, CancellationToken ct = default);

    /// <summary>Validates a graph without saving it. Backs the editor's live linter.</summary>
    PipelineValidateResponse Validate(PipelineValidateRequest request);

    /// <summary>
    /// Inserts a Queued run and returns its id. Created before anything is enqueued so it appears in the UI
    /// immediately and so the worker has a row to claim rather than one to invent.
    /// </summary>
    /// <param name="nodeIds">
    /// For a partial run, the steps the operator selected. Null or empty runs everything. Only a manual run
    /// may narrow its scope — a cron or API trigger that quietly ran a subset would be a pipeline that
    /// looks scheduled and is not.
    /// </param>
    Task<PipelineRunCreation> CreateQueuedRunAsync(
        string companyId, string pipelineId, string triggerType, string? triggeredBy,
        Dictionary<string, string>? parameters, IReadOnlyList<string>? nodeIds = null,
        CancellationToken ct = default);

    Task SetRunJobIdAsync(string runId, string jobId, CancellationToken ct = default);

    /// <summary>
    /// Marks a run cancelled. The engine notices at the next step boundary — cooperative, because killing a
    /// run mid-write is how you get a half-loaded table.
    /// </summary>
    Task<bool> CancelRunAsync(string companyId, string runId, CancellationToken ct = default);

    Task<List<PipelineRunDto>> GetRunsAsync(string companyId, string? pipelineId, int take, CancellationToken ct = default);
    Task<PipelineRunDto?> GetRunAsync(string companyId, string runId, CancellationToken ct = default);
    Task<List<PipelineRunStepDto>> GetStepsAsync(string companyId, string runId, CancellationToken ct = default);
    Task<PipelineRunStepDto?> GetStepAsync(string companyId, string runId, string nodeId, CancellationToken ct = default);
    Task<PipelineRunStatusDto?> GetRunStatusAsync(string companyId, string runId, int since, CancellationToken ct = default);

    /// <summary>
    /// Each step's output columns from the most recent run that produced any. The editor merges these into
    /// its schema cache, which is what lets a mapping grid list real column names.
    /// </summary>
    Task<Dictionary<string, List<PipelineColumn>>> GetSchemasAsync(string companyId, string pipelineId, CancellationToken ct = default);

    /// <summary>Fails runs whose runner stopped heartbeating, so they don't sit Running forever.</summary>
    Task<int> FailStaleRunsAsync(TimeSpan olderThan, CancellationToken ct = default);

    /// <summary>Hard-deletes old step rows. The one place this schema does not soft-delete.</summary>
    Task<int> PurgeOldStepsAsync(int retentionDays, CancellationToken ct = default);
}

public sealed record PipelineRunCreation(bool Success, string? RunId, string? Error)
{
    public static PipelineRunCreation Failed(string error) => new(false, null, error);
}

public class PipelineService(ApplicationDbContext db, PipelineOptions options) : IPipelineService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------- reads

    public async Task<List<PipelineDto>> GetAllAsync(string companyId, CancellationToken ct = default)
    {
        // graph_json and validation_json are projected out; see PipelineDto for why.
        var rows = await db.Pipeline.AsNoTracking()
            .Where(p => p.CompanyId == companyId)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id, p.Name, p.Description, p.IsEnabled, p.ApiEnabled, p.CronExpression, p.TimeZone,
                p.NodeCount, p.Valid, p.ValidationJson, p.LastRunAt, p.LastRunStatus, p.LastRunMessage,
                p.LastRunRows, p.RunCount, p.CreatedAt, p.CreatedBy, p.ModifiedAt
            })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var issues = ParseIssues(r.ValidationJson);
            return new PipelineDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsEnabled = r.IsEnabled,
                ApiEnabled = r.ApiEnabled,
                CronExpression = r.CronExpression,
                TimeZone = r.TimeZone,
                NodeCount = r.NodeCount,
                Valid = r.Valid,
                ErrorCount = issues.Count(i => i.Severity == PipelineIssueSeverity.Error),
                WarningCount = issues.Count(i => i.Severity == PipelineIssueSeverity.Warning),
                LastRunAt = r.LastRunAt,
                LastRunStatus = r.LastRunStatus,
                LastRunMessage = r.LastRunMessage,
                LastRunRows = r.LastRunRows,
                RunCount = r.RunCount,
                CreatedAt = r.CreatedAt,
                CreatedBy = r.CreatedBy,
                ModifiedAt = r.ModifiedAt,
                ScheduleState = DeriveScheduleState(r.IsEnabled, r.CronExpression, r.Valid)
            };
        }).ToList();
    }

    public async Task<PipelineDetailDto?> GetAsync(string companyId, string id, CancellationToken ct = default)
    {
        var row = await db.Pipeline.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == id, ct);

        return row is null ? null : ToDetail(row);
    }

    // ------------------------------------------------------------------ writes

    public async Task<PipelineSaveResult> CreateAsync(
        string companyId, string? userId, PipelineSaveRequest request, CancellationToken ct = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return PipelineSaveResult.Failed("A pipeline needs a name.");

        if (await NameTakenAsync(companyId, name, null, ct))
            return PipelineSaveResult.Failed($"A pipeline called '{name}' already exists.");

        var pipeline = new Pipeline
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            Name = name,
            Description = request.Description?.Trim(),
            GraphJson = request.GraphJson ?? PipelineGraph.NewDefault().Serialize(),
            IsEnabled = request.IsEnabled,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow
        };

        var applied = ApplyTriggerSettings(pipeline, request);
        if (applied is not null) return PipelineSaveResult.Failed(applied);

        ApplyCompileResults(pipeline);

        db.Pipeline.Add(pipeline);
        await db.SaveChangesAsync(ct);

        return PipelineSaveResult.Ok(ToDetail(pipeline));
    }

    public async Task<PipelineSaveResult> UpdateAsync(
        string companyId, string? userId, string id, PipelineSaveRequest request, CancellationToken ct = default)
    {
        var pipeline = await db.Pipeline.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == id, ct);
        if (pipeline is null) return PipelineSaveResult.Failed("That pipeline no longer exists.");

        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return PipelineSaveResult.Failed("A pipeline needs a name.");

        if (await NameTakenAsync(companyId, name, id, ct))
            return PipelineSaveResult.Failed($"A pipeline called '{name}' already exists.");

        pipeline.Name = name;
        pipeline.Description = request.Description?.Trim();
        pipeline.GraphJson = request.GraphJson;
        pipeline.IsEnabled = request.IsEnabled;
        pipeline.ModifiedAt = DateTime.UtcNow;
        pipeline.ModifiedBy = userId;

        var applied = ApplyTriggerSettings(pipeline, request);
        if (applied is not null) return PipelineSaveResult.Failed(applied);

        ApplyCompileResults(pipeline);
        await db.SaveChangesAsync(ct);

        return PipelineSaveResult.Ok(ToDetail(pipeline));
    }

    public async Task<bool> DeleteAsync(string companyId, string id, CancellationToken ct = default)
    {
        var pipeline = await db.Pipeline.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == id, ct);
        if (pipeline is null) return false;

        // Runs are deliberately left behind: they are the audit record of what happened to real data, and
        // they carry their own pipeline name and graph so they stay readable without this row.
        //
        // Watermarks are NOT left behind. There is no FK to cascade (one would make a pipeline with history
        // undeletable), so without this the rows would linger and a later pipeline reusing the id would
        // inherit a stranger's high-water mark and silently skip its first load.
        var state = await db.PipelineState
            .Where(x => x.CompanyId == companyId && x.PipelineId == id)
            .ToListAsync(ct);

        if (state.Count > 0) db.PipelineState.RemoveRange(state);

        db.Pipeline.Remove(pipeline);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> ResetStateAsync(string companyId, string id, CancellationToken ct = default)
    {
        var state = await db.PipelineState
            .Where(x => x.CompanyId == companyId && x.PipelineId == id)
            .ToListAsync(ct);

        if (state.Count == 0) return 0;

        db.PipelineState.RemoveRange(state);
        await db.SaveChangesAsync(ct);
        return state.Count;
    }

    public async Task<PipelineSaveResult> DuplicateAsync(
        string companyId, string? userId, string id, CancellationToken ct = default)
    {
        var source = await db.Pipeline.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == id, ct);
        if (source is null) return PipelineSaveResult.Failed("That pipeline no longer exists.");

        var name = await NextCopyNameAsync(companyId, source.Name, ct);

        // The copy is created without a schedule on purpose. Duplicating a nightly load and having two of
        // them start running is a genuinely bad surprise.
        return await CreateAsync(companyId, userId, new PipelineSaveRequest
        {
            Name = name,
            Description = source.Description,
            GraphJson = source.GraphJson,
            IsEnabled = false
        }, ct);
    }

    public PipelineValidateResponse Validate(PipelineValidateRequest request)
    {
        var compiled = PipelineCompiler.Compile(
            request.GraphJson, options.ResolveMaxNodes(), request.Scheduled);

        return new PipelineValidateResponse
        {
            Valid = compiled.Valid,
            RequiresManualRun = compiled.Graph?.RequiresManualRun ?? false,
            Issues = compiled.Issues.Select(ToIssueDto).ToList(),
            Order = compiled.Graph?.Order.ToList() ?? new()
        };
    }

    // -------------------------------------------------------------------- runs

    public async Task<PipelineRunCreation> CreateQueuedRunAsync(
        string companyId, string pipelineId, string triggerType, string? triggeredBy,
        Dictionary<string, string>? parameters, IReadOnlyList<string>? nodeIds = null,
        CancellationToken ct = default)
    {
        var pipeline = await db.Pipeline.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == pipelineId, ct);

        if (pipeline is null) return PipelineRunCreation.Failed("That pipeline no longer exists.");
        if (!pipeline.IsEnabled) return PipelineRunCreation.Failed("This pipeline is disabled.");

        // Compiled here as well as at save, because "valid" is a stored flag and this is the last chance to
        // refuse before a run row exists. A scheduled or API run also has to satisfy the schedule-only rules.
        var scheduled = triggerType != PipelineTriggerType.Manual;
        var compiled = PipelineCompiler.Compile(pipeline.GraphJson, options.ResolveMaxNodes(), scheduled);

        if (!compiled.Valid)
        {
            var first = compiled.Errors.FirstOrDefault();
            return PipelineRunCreation.Failed(
                first is null
                    ? "This pipeline is not valid yet."
                    : $"This pipeline cannot run: {first.Message}");
        }

        // A narrowed scope is a manual-only affordance. Allowing it on a cron would produce a pipeline that
        // appears scheduled while silently never running some of its steps.
        var selected = nodeIds?.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToList();

        if (selected is { Count: > 0 })
        {
            if (scheduled)
            {
                return PipelineRunCreation.Failed(
                    "Only a manual run can run a subset of the steps.");
            }

            var unknown = compiled.Graph!.UnknownIds(selected);
            if (unknown.Count > 0)
            {
                // Refused, not narrowed — see PipelineEngine's matching check. A stale selection must not
                // quietly run something other than what was asked for.
                return PipelineRunCreation.Failed(
                    $"These steps are no longer in the pipeline: {string.Join(", ", unknown)}. "
                    + "Reload the editor and select again.");
            }
        }

        var run = new PipelineRun
        {
            Id = Guid.NewGuid().ToString(),
            PipelineId = pipeline.Id,
            CompanyId = companyId,
            PipelineName = pipeline.Name,
            Status = PipelineRunStatus.Queued,
            TriggerType = triggerType,
            TriggeredBy = triggeredBy,
            // Snapshotted, so editing the pipeline afterwards cannot change what this run appears to have done.
            GraphJson = pipeline.GraphJson,
            ParamsJson = parameters is { Count: > 0 } ? JsonSerializer.Serialize(parameters, Json) : null,
            SelectedNodesJson = selected is { Count: > 0 }
                ? JsonSerializer.Serialize(selected, Json)
                : null,
            StartedAt = DateTime.UtcNow
        };

        db.PipelineRun.Add(run);
        await db.SaveChangesAsync(ct);

        return new PipelineRunCreation(true, run.Id, null);
    }

    public async Task SetRunJobIdAsync(string runId, string jobId, CancellationToken ct = default)
    {
        await db.PipelineRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, jobId), ct);
    }

    public async Task<bool> CancelRunAsync(string companyId, string runId, CancellationToken ct = default)
    {
        // Only a run that has not finished. Flipping a completed run to Canceled would rewrite history.
        var updated = await db.PipelineRun
            .Where(r => r.Id == runId && r.CompanyId == companyId
                        && (r.Status == PipelineRunStatus.Queued || r.Status == PipelineRunStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, PipelineRunStatus.Canceled)
                .SetProperty(r => r.Error, "Cancelled.")
                .SetProperty(r => r.ErrorType, PipelineErrorType.Canceled)
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow), ct);

        return updated > 0;
    }

    public async Task<List<PipelineRunDto>> GetRunsAsync(
        string companyId, string? pipelineId, int take, CancellationToken ct = default)
    {
        var query = db.PipelineRun.AsNoTracking().Where(r => r.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(pipelineId)) query = query.Where(r => r.PipelineId == pipelineId);

        var rows = await query
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(take, 1, 500))
            // Log and the graph/params blobs are excluded: a history page of 50 runs does not need 50 logs.
            // The selection column IS fetched — it is a short array, and without it a partial run is
            // indistinguishable from a run that lost most of its steps.
            .Select(r => new { Selected = r.SelectedNodesJson, Dto = new PipelineRunDto
            {
                Id = r.Id,
                PipelineId = r.PipelineId,
                PipelineName = r.PipelineName,
                Status = r.Status,
                TriggerType = r.TriggerType,
                TriggeredBy = r.TriggeredBy,
                Error = r.Error,
                ErrorType = r.ErrorType,
                ErrorNodeId = r.ErrorNodeId,
                StepsTotal = r.StepsTotal,
                StepsCompleted = r.StepsCompleted,
                StepsFailed = r.StepsFailed,
                StepsSkipped = r.StepsSkipped,
                RowsRead = r.RowsRead,
                RowsWritten = r.RowsWritten,
                DurationMs = r.DurationMs,
                JobId = r.JobId,
                StartedAt = r.StartedAt,
                FinishedAt = r.FinishedAt
            } })
            .ToListAsync(ct);

        // Parsed out here rather than in the projection: that Select is translated to SQL and cannot call a
        // deserializer.
        foreach (var row in rows) row.Dto.SelectedNodeIds = ParseNodeIds(row.Selected);

        return rows.Select(r => r.Dto).ToList();
    }

    public async Task<PipelineRunDto?> GetRunAsync(string companyId, string runId, CancellationToken ct = default)
    {
        var run = await db.PipelineRun.AsNoTracking()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == runId, ct);

        if (run is null) return null;

        return new PipelineRunDto
        {
            Id = run.Id,
            PipelineId = run.PipelineId,
            PipelineName = run.PipelineName,
            Status = run.Status,
            TriggerType = run.TriggerType,
            TriggeredBy = run.TriggeredBy,
            Error = run.Error,
            ErrorType = run.ErrorType,
            ErrorNodeId = run.ErrorNodeId,
            StepsTotal = run.StepsTotal,
            StepsCompleted = run.StepsCompleted,
            StepsFailed = run.StepsFailed,
            StepsSkipped = run.StepsSkipped,
            RowsRead = run.RowsRead,
            RowsWritten = run.RowsWritten,
            DurationMs = run.DurationMs,
            JobId = run.JobId,
            Log = run.Log,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            SelectedNodeIds = ParseNodeIds(run.SelectedNodesJson)
        };
    }

    /// <summary>
    /// The stored selection for a partial run. Never throws: an unreadable value means "we cannot say this
    /// was partial", which shows the run as an ordinary one rather than failing the whole history page.
    /// </summary>
    private static List<string> ParseNodeIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)
                       ?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList()
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<PipelineRunStepDto>> GetStepsAsync(
        string companyId, string runId, CancellationToken ct = default)
    {
        // Preview and column JSON are omitted here and fetched per step on demand — a 40-step run would
        // otherwise ship 40 result previews to draw one bar chart.
        return await db.PipelineRunStep.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.RunId == runId)
            .OrderBy(s => s.StepIndex)
            .Select(s => new PipelineRunStepDto
            {
                Id = s.Id,
                NodeId = s.NodeId,
                NodeType = s.NodeType,
                NodeLabel = s.NodeLabel,
                StepIndex = s.StepIndex,
                Status = s.Status,
                RowsOut = s.RowsOut,
                SqlText = s.SqlText,
                Error = s.Error,
                ErrorType = s.ErrorType,
                DurationMs = s.DurationMs,
                StartedAt = s.StartedAt,
                CompletedAt = s.CompletedAt
            })
            .ToListAsync(ct);
    }

    public async Task<PipelineRunStepDto?> GetStepAsync(
        string companyId, string runId, string nodeId, CancellationToken ct = default)
    {
        var step = await db.PipelineRunStep.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.RunId == runId && s.NodeId == nodeId)
            .OrderByDescending(s => s.StepIndex)
            .FirstOrDefaultAsync(ct);

        if (step is null) return null;

        return new PipelineRunStepDto
        {
            Id = step.Id,
            NodeId = step.NodeId,
            NodeType = step.NodeType,
            NodeLabel = step.NodeLabel,
            StepIndex = step.StepIndex,
            Status = step.Status,
            RowsOut = step.RowsOut,
            SqlText = step.SqlText,
            Error = step.Error,
            ErrorType = step.ErrorType,
            DurationMs = step.DurationMs,
            StartedAt = step.StartedAt,
            CompletedAt = step.CompletedAt,
            Preview = Deserialize<List<Dictionary<string, object?>>>(step.OutputPreviewJson),
            Columns = Deserialize<List<PipelineColumn>>(step.OutputColumnsJson)
        };
    }

    public async Task<PipelineRunStatusDto?> GetRunStatusAsync(
        string companyId, string runId, int since, CancellationToken ct = default)
    {
        var run = await db.PipelineRun.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.Id == runId)
            .Select(r => new
            {
                r.Id, r.Status, r.StepsTotal, r.StepsCompleted, r.StepsFailed, r.StepsSkipped,
                r.RowsRead, r.RowsWritten, r.DurationMs, r.Error, r.ErrorType, r.ErrorNodeId,
                r.StartedAt, r.FinishedAt
            })
            .FirstOrDefaultAsync(ct);

        if (run is null) return null;

        var ticks = await db.PipelineRunStep.AsNoTracking()
            // A running step is re-sent on EVERY poll, not just once. The cursor exists so a finished
            // step is delivered a single time — but a step in progress has a row count that keeps
            // changing, and under the cursor alone it would be sent once and then appear frozen.
            .Where(s => s.RunId == runId && (s.StepIndex > since || s.Status == PipelineStepStatus.Running))
            .OrderBy(s => s.StepIndex)
            .Select(s => new PipelineStepTickDto
            {
                NodeId = s.NodeId,
                Status = s.Status,
                RowsOut = s.RowsOut,
                DurationMs = s.DurationMs,
                Error = s.Error,
                StepIndex = s.StepIndex
            })
            .ToListAsync(ct);

        return new PipelineRunStatusDto
        {
            Id = run.Id,
            Status = run.Status,
            Rev = PipelineStepTicks.Revision(run.StepsCompleted, run.StepsFailed, run.StepsSkipped),
            StepsTotal = run.StepsTotal,
            StepsCompleted = run.StepsCompleted,
            StepsFailed = run.StepsFailed,
            StepsSkipped = run.StepsSkipped,
            RowsRead = run.RowsRead,
            RowsWritten = run.RowsWritten,
            DurationMs = run.DurationMs,
            Error = run.Error,
            ErrorType = run.ErrorType,
            ErrorNodeId = run.ErrorNodeId,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            Cursor = PipelineStepTicks.NextCursor(ticks, since),
            Steps = ticks
        };
    }

    public async Task<Dictionary<string, List<PipelineColumn>>> GetSchemasAsync(
        string companyId, string pipelineId, CancellationToken ct = default)
    {
        // The newest recorded columns per node, across runs. Taking the newest per node rather than from a
        // single run means a partially-failed run still contributes what it did manage to produce.
        var rows = await db.PipelineRunStep.AsNoTracking()
            .Where(s => s.CompanyId == companyId
                        && s.PipelineId == pipelineId
                        && s.OutputColumnsJson != null)
            .OrderByDescending(s => s.CompletedAt)
            .Select(s => new { s.NodeId, s.OutputColumnsJson })
            .Take(500)
            .ToListAsync(ct);

        var schemas = new Dictionary<string, List<PipelineColumn>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (schemas.ContainsKey(row.NodeId)) continue;
            var columns = Deserialize<List<PipelineColumn>>(row.OutputColumnsJson);
            if (columns is { Count: > 0 }) schemas[row.NodeId] = columns;
        }

        return schemas;
    }

    // ------------------------------------------------------------ housekeeping

    public async Task<int> FailStaleRunsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;

        // Heartbeat-based, not a blanket sweep of everything Running: restarting one process while another
        // is mid-run must not fail that other run. A null heartbeat falls back to StartedAt, which covers a
        // run that died before its first checkpoint.
        //
        // Known window: the engine heartbeats between steps, so a single step longer than the threshold can
        // be marked abandoned while it is in fact still working. It self-corrects — the engine writes the
        // true outcome when it finishes — but it is why the default threshold is hours rather than minutes.
        return await db.PipelineRun
            .Where(r => r.Status == PipelineRunStatus.Running
                        && (r.HeartbeatAt ?? r.StartedAt) < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, PipelineRunStatus.Failed)
                .SetProperty(r => r.Error,
                    "The runner stopped responding, so this run was abandoned. It may have been interrupted by a restart.")
                .SetProperty(r => r.ErrorType, PipelineErrorType.Unknown)
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow), ct);
    }

    public async Task<int> PurgeOldStepsAsync(int retentionDays, CancellationToken ct = default)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        // A genuine hard delete, and the only one in this schema. Step rows are per-node telemetry, not user
        // data — a busy pipeline produces millions of them a year and nobody wants them back.
        return await db.PipelineRunStep
            .Where(s => s.StartedAt != null && s.StartedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Applies the trigger fields, refusing combinations that cannot work. Returns an error message, or
    /// null when everything is fine.
    /// </summary>
    private string? ApplyTriggerSettings(Pipeline pipeline, PipelineSaveRequest request)
    {
        var cron = string.IsNullOrWhiteSpace(request.CronExpression) ? null : request.CronExpression!.Trim();

        if (cron is not null && !LooksLikeCron(cron))
            return $"'{cron}' is not a valid schedule. Use five fields, for example '0 3 * * *' for 3am daily.";

        // The rule that earns the compiler's `scheduled` parameter: an uploaded file exists for exactly one
        // run, so a pipeline that reads one cannot be given an unattended trigger. Caught here rather than
        // at 3am, when the answer would be a failed run and no file.
        var wantsUnattended = cron is not null || request.ApiEnabled;
        if (wantsUnattended)
        {
            var compiled = PipelineCompiler.Compile(pipeline.GraphJson, options.ResolveMaxNodes(), scheduled: true);
            var blocking = compiled.Issues.FirstOrDefault(
                i => i.Code == PipelineIssueCodes.NodeUploadNotSchedulable);

            if (blocking is not null) return blocking.Message;
        }

        pipeline.CronExpression = cron;
        pipeline.TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone!.Trim();
        pipeline.ApiEnabled = request.ApiEnabled;
        return null;
    }

    /// <summary>
    /// A deliberately shallow cron check: field count and character set. Hangfire does the real parse when
    /// the registrar schedules it, and logs per-pipeline if it disagrees — so this only needs to catch the
    /// typos a person actually makes, without this service taking on a cron grammar.
    /// </summary>
    private static bool LooksLikeCron(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length is not (5 or 6)) return false;

        foreach (var field in fields)
        {
            foreach (var c in field)
            {
                if (char.IsAsciiDigit(c)) continue;
                if (c is '*' or ',' or '-' or '/' or '?' or 'L' or 'W' or '#') continue;
                if (char.IsAsciiLetter(c)) continue;   // MON, JAN and friends
                return false;
            }
        }

        return true;
    }

    private void ApplyCompileResults(Pipeline pipeline)
    {
        var scheduled = pipeline.CronExpression is not null || pipeline.ApiEnabled;
        var compiled = PipelineCompiler.Compile(pipeline.GraphJson, options.ResolveMaxNodes(), scheduled);
        var graph = PipelineGraph.TryParse(pipeline.GraphJson);

        pipeline.NodeCount = graph?.Nodes.Count ?? 0;
        pipeline.SchemaVersion = graph?.SchemaVersion ?? PipelineGraph.CurrentSchemaVersion;
        pipeline.Valid = compiled.Valid;
        pipeline.ValidationJson = compiled.Issues.Count == 0
            ? null
            : JsonSerializer.Serialize(compiled.Issues.Select(ToIssueDto).ToList(), Json);
    }

    private async Task<bool> NameTakenAsync(string companyId, string name, string? exceptId, CancellationToken ct) =>
        await db.Pipeline.AsNoTracking().AnyAsync(
            p => p.CompanyId == companyId && p.Name == name && (exceptId == null || p.Id != exceptId), ct);

    private async Task<string> NextCopyNameAsync(string companyId, string baseName, CancellationToken ct)
    {
        var candidate = $"{baseName} copy";
        var suffix = 2;

        while (await NameTakenAsync(companyId, candidate, null, ct))
        {
            candidate = $"{baseName} copy {suffix++}";
            if (suffix > 50) return $"{baseName} copy {Guid.NewGuid().ToString("N")[..6]}";
        }

        return candidate;
    }

    private static string DeriveScheduleState(bool enabled, string? cron, bool valid)
    {
        if (!valid) return "Error";
        if (!enabled) return "Paused";
        return string.IsNullOrWhiteSpace(cron) ? "Manual" : "Active";
    }

    private PipelineDetailDto ToDetail(Pipeline p)
    {
        var issues = ParseIssues(p.ValidationJson);
        var compiled = PipelineCompiler.Compile(p.GraphJson, options.ResolveMaxNodes());

        return new PipelineDetailDto
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            GraphJson = p.GraphJson,
            IsEnabled = p.IsEnabled,
            ApiEnabled = p.ApiEnabled,
            CronExpression = p.CronExpression,
            TimeZone = p.TimeZone,
            NodeCount = p.NodeCount,
            Valid = p.Valid,
            ErrorCount = issues.Count(i => i.Severity == PipelineIssueSeverity.Error),
            WarningCount = issues.Count(i => i.Severity == PipelineIssueSeverity.Warning),
            Issues = issues,
            LastRunAt = p.LastRunAt,
            LastRunStatus = p.LastRunStatus,
            LastRunMessage = p.LastRunMessage,
            LastRunRows = p.LastRunRows,
            RunCount = p.RunCount,
            CreatedAt = p.CreatedAt,
            CreatedBy = p.CreatedBy,
            ModifiedAt = p.ModifiedAt,
            ScheduleState = DeriveScheduleState(p.IsEnabled, p.CronExpression, p.Valid),
            RequiresManualRun = compiled.Graph?.RequiresManualRun ?? false
        };
    }

    private static PipelineIssueDto ToIssueDto(PipelineValidationIssue issue) => new()
    {
        NodeId = issue.NodeId,
        Code = issue.Code,
        Message = issue.Message,
        Severity = issue.Severity
    };

    private static List<PipelineIssueDto> ParseIssues(string? json) =>
        Deserialize<List<PipelineIssueDto>>(json) ?? new();

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }
}

using System.Text.Json.Serialization;

namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// A pipeline as the list page sees it. <b>Deliberately without the graph:</b> a page of ten pipelines
/// would otherwise ship ten full graph documents to a browser that only wants to draw ten table rows.
/// </summary>
public class PipelineDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool IsEnabled { get; set; }
    public bool ApiEnabled { get; set; }
    public string? CronExpression { get; set; }
    public string? TimeZone { get; set; }

    public int NodeCount { get; set; }
    public bool Valid { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }

    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastRunMessage { get; set; }
    public long? LastRunRows { get; set; }
    public int RunCount { get; set; }

    /// <summary>Filled from Hangfire at read time, like the ingestion page does — never stored.</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>Active / Paused / Manual / Error, derived for the list badge.</summary>
    public string? ScheduleState { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }

    /// <summary>Set when a source reads a file uploaded at run time, which cannot be scheduled.</summary>
    public bool RequiresManualRun { get; set; }
}

/// <summary>A pipeline plus its graph — what the editor loads.</summary>
public class PipelineDetailDto : PipelineDto
{
    public string? GraphJson { get; set; }
    public List<PipelineIssueDto> Issues { get; set; } = new();
}

public class PipelineIssueDto
{
    public string? NodeId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
}

/// <summary>Create/update payload. A pipeline may be saved invalid — it just cannot run.</summary>
public class PipelineSaveRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? GraphJson { get; set; }

    public bool IsEnabled { get; set; } = true;
    public bool ApiEnabled { get; set; }
    public string? CronExpression { get; set; }
    public string? TimeZone { get; set; }
}

/// <summary>Outcome of a save: the row, or why it was refused.</summary>
public class PipelineSaveResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public PipelineDetailDto? Pipeline { get; set; }

    public static PipelineSaveResult Failed(string error) => new() { Error = error };
    public static PipelineSaveResult Ok(PipelineDetailDto pipeline) => new() { Success = true, Pipeline = pipeline };
}

/// <summary>One run, for the history table and the run header.</summary>
public class PipelineRunDto
{
    public string Id { get; set; } = string.Empty;
    public string PipelineId { get; set; } = string.Empty;
    public string? PipelineName { get; set; }

    public string Status { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string? TriggeredBy { get; set; }

    public string? Error { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorNodeId { get; set; }

    public int StepsTotal { get; set; }
    public int StepsCompleted { get; set; }
    public int StepsFailed { get; set; }
    public int StepsSkipped { get; set; }

    public long RowsRead { get; set; }
    public long RowsWritten { get; set; }
    public int DurationMs { get; set; }

    public string? JobId { get; set; }
    public string? Log { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Deep link into the Hangfire dashboard, when one is configured.</summary>
    public string? JobUrl { get; set; }

    /// <summary>
    /// For a partial run, the steps that were selected. Empty means the whole pipeline ran.
    /// <para>
    /// Carried on the history row too, not just the detail: a partial run has a smaller StepsTotal than the
    /// pipeline has nodes, and without this a reader has no way to tell that apart from a run that lost
    /// steps to a failure.
    /// </para>
    /// </summary>
    public List<string> SelectedNodeIds { get; set; } = new();

    /// <summary>True when this run deliberately ran a subset of the pipeline.</summary>
    public bool IsPartial => SelectedNodeIds.Count > 0;
}

/// <summary>One step of a run, for the waterfall and the per-step inspector.</summary>
public class PipelineRunStepDto
{
    public string Id { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string? NodeType { get; set; }
    public string? NodeLabel { get; set; }

    public int StepIndex { get; set; }
    public string Status { get; set; } = string.Empty;

    public long? RowsOut { get; set; }
    public string? SqlText { get; set; }
    public string? Error { get; set; }
    public string? ErrorType { get; set; }
    public int DurationMs { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>A few rows of what this step produced. Fetched only when a step is opened.</summary>
    public List<Dictionary<string, object?>>? Preview { get; set; }
    public List<PipelineColumn>? Columns { get; set; }
}

/// <summary>
/// The polling payload, kept small on purpose: short property names, only steps newer than the caller's
/// cursor, and a monotonic <see cref="Rev"/> so an unchanged poll costs the browser no re-render at all.
/// </summary>
public class PipelineRunStatusDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Sum of non-decreasing counters. Equal to the last poll means nothing has changed; lower means an
    /// out-of-order response that can simply be discarded.
    /// </summary>
    [JsonPropertyName("rev")] public int Rev { get; set; }

    [JsonPropertyName("total")] public int StepsTotal { get; set; }
    [JsonPropertyName("done")] public int StepsCompleted { get; set; }
    [JsonPropertyName("failed")] public int StepsFailed { get; set; }
    [JsonPropertyName("skipped")] public int StepsSkipped { get; set; }

    [JsonPropertyName("read")] public long RowsRead { get; set; }
    [JsonPropertyName("written")] public long RowsWritten { get; set; }
    [JsonPropertyName("ms")] public int DurationMs { get; set; }

    [JsonPropertyName("err")] public string? Error { get; set; }
    [JsonPropertyName("errType")] public string? ErrorType { get; set; }
    [JsonPropertyName("errNode")] public string? ErrorNodeId { get; set; }

    [JsonPropertyName("startedAt")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("finishedAt")] public DateTime? FinishedAt { get; set; }

    /// <summary>Pass as <c>since</c> on the next poll.</summary>
    [JsonPropertyName("cursor")] public int Cursor { get; set; }

    [JsonPropertyName("steps")] public List<PipelineStepTickDto> Steps { get; set; } = new();
}

public class PipelineStepTickDto
{
    [JsonPropertyName("n")] public string NodeId { get; set; } = string.Empty;
    [JsonPropertyName("s")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("r")] public long? RowsOut { get; set; }
    [JsonPropertyName("ms")] public int DurationMs { get; set; }
    [JsonPropertyName("e")] public string? Error { get; set; }
    [JsonPropertyName("seq")] public int StepIndex { get; set; }
}

/// <summary>Live-lint payload: a graph, no save required.</summary>
public class PipelineValidateRequest
{
    public string? GraphJson { get; set; }

    /// <summary>Validate as if it were about to be scheduled, so schedule-only rules apply.</summary>
    public bool Scheduled { get; set; }
}

public class PipelineValidateResponse
{
    public bool Valid { get; set; }
    public bool RequiresManualRun { get; set; }
    public List<PipelineIssueDto> Issues { get; set; } = new();

    /// <summary>Topological step order, so the editor can number steps the way a run will.</summary>
    public List<string> Order { get; set; } = new();
}

/// <summary>Convert between the stored JSON graph and its YAML view.</summary>
public class PipelineYamlRequest
{
    public string? GraphJson { get; set; }
    public string? Yaml { get; set; }
}

public class PipelineYamlResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Yaml { get; set; }
    public string? GraphJson { get; set; }
}

/// <summary>Body for starting a run.</summary>
public class PipelineRunRequest
{
    /// <summary>Values available to step config as <c>{{ params.* }}</c>.</summary>
    public Dictionary<string, string>? Params { get; set; }

    /// <summary>Run the saved draft even if it has validation warnings.</summary>
    public bool UseDraft { get; set; } = true;

    /// <summary>
    /// Run only these steps. Empty or absent runs the whole pipeline.
    /// <para>
    /// The engine also runs whatever these steps need: a step's input is a relation in the run's scratch
    /// database, which is created per run and deleted afterwards, so there is no earlier output for a
    /// mid-graph step to read. Send the selection; the closure is computed server-side, because the client
    /// must not be the thing that decides which steps a run may skip.
    /// </para>
    /// <para>
    /// A destination in this list <b>writes or sends for real</b>. Destinations are terminal so one can
    /// never be added automatically as another step's input — it runs only when named here.
    /// </para>
    /// </summary>
    public List<string>? NodeIds { get; set; }
}

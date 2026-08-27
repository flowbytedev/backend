using System;
using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// A saved ETL pipeline: a graph of sources, transforms and destinations, plus how it is triggered.
/// <para>
/// The graph itself lives as JSON in <see cref="GraphJson"/> rather than as rows. A pipeline is edited and
/// versioned as one document, is always read whole, and its node shapes differ per type — three properties
/// that a node table and an edge table would fight rather than help. This is the same call
/// <c>IngestionSource.SourceConfig</c> already makes for its kind-specific settings.
/// </para>
/// <para>
/// <see cref="NodeCount"/>, <see cref="Valid"/> and <see cref="ValidationJson"/> are denormalized on save
/// so the list page can show status without parsing every graph — a page of ten full graphs is a lot of
/// JSON to send to a browser that only wants to draw ten rows.
/// </para>
/// </summary>
public class Pipeline
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string CompanyId { get; set; } = string.Empty;

    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>The graph document. See <see cref="PipelineGraph"/>.</summary>
    public string? GraphJson { get; set; }

    public int SchemaVersion { get; set; } = PipelineGraph.CurrentSchemaVersion;

    // ---- Triggers. All three drive the same graph; none of them is a node in it. ----

    /// <summary>Master switch. A disabled pipeline is not scheduled and refuses API runs.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Allows <c>POST api/public/pipelines/{id}/runs</c> with an API key.</summary>
    public bool ApiEnabled { get; set; }

    /// <summary>
    /// 5-field cron, or null for manual only. Registered with Hangfire by the registrar job, which is why
    /// this is a plain column and not a Hangfire concept — the recurring jobs are reconciled from these
    /// rows, so the database stays the source of truth even if Hangfire's own storage is rebuilt.
    /// </summary>
    [StringLength(120)]
    public string? CronExpression { get; set; }

    [StringLength(80)]
    public string? TimeZone { get; set; }

    public DateTime? NextRunAt { get; set; }

    // ---- Denormalized run summary, for the list page. ----

    public DateTime? LastRunAt { get; set; }

    [StringLength(40)]
    public string? LastRunStatus { get; set; }

    public string? LastRunMessage { get; set; }
    public long? LastRunRows { get; set; }
    public int RunCount { get; set; }

    // ---- Denormalized compile results, refreshed on every save. ----

    public int NodeCount { get; set; }

    /// <summary>
    /// False when the graph has validation errors. A pipeline is deliberately allowed to be saved invalid
    /// — half-built work should not be lost — it just cannot run.
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>Serialized <c>PipelineValidationIssue[]</c>, so the list page can explain why.</summary>
    public string? ValidationJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>
/// One execution of a <see cref="Pipeline"/>.
/// <para>
/// Deliberately has <b>no foreign key</b> to <c>pipeline</c>, unlike <c>ingestion_run</c>. A run is the
/// audit record of something that actually happened to real data, so it has to survive the pipeline being
/// deleted — and with <c>DeleteBehavior.Restrict</c> everywhere, an FK would instead make a pipeline with
/// history undeletable. <see cref="PipelineName"/> and <see cref="GraphJson"/> are copied onto the run for
/// the same reason: it stays readable and reproducible on its own.
/// </para>
/// </summary>
public class PipelineRun
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string PipelineId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;

    /// <summary>Copied at enqueue, so history reads correctly after a rename or a delete.</summary>
    [StringLength(150)]
    public string? PipelineName { get; set; }

    [Required]
    [StringLength(16)]
    public string Status { get; set; } = PipelineRunStatus.Queued;

    [Required]
    [StringLength(16)]
    public string TriggerType { get; set; } = PipelineTriggerType.Manual;

    /// <summary>Who or what started it — a user id, or the api key id for an API run.</summary>
    [StringLength(450)]
    public string? TriggeredBy { get; set; }

    /// <summary>
    /// The graph that actually executed. Editing the pipeline afterwards must not change what a past run
    /// appears to have done.
    /// </summary>
    public string? GraphJson { get; set; }

    /// <summary>Run parameters, available to config as <c>{{ params.* }}</c>.</summary>
    public string? ParamsJson { get; set; }

    /// <summary>
    /// The hidden scratch dataset this run used. Kept so the sweeper can clean up after a crash, and so a
    /// failed run's intermediate tables can be inspected while they are retained.
    /// </summary>
    [StringLength(450)]
    public string? ScratchDatasetId { get; set; }

    public string? Error { get; set; }

    /// <summary>A <see cref="PipelineErrorType"/> value, so the UI can style a drift or lock failure distinctly.</summary>
    [StringLength(64)]
    public string? ErrorType { get; set; }

    [StringLength(128)]
    public string? ErrorNodeId { get; set; }

    public int StepsTotal { get; set; }
    public int StepsCompleted { get; set; }
    public int StepsFailed { get; set; }
    public int StepsSkipped { get; set; }

    public long RowsRead { get; set; }
    public long RowsWritten { get; set; }

    /// <summary>
    /// Rows the run could not parse and skipped. Separate from RowsRead because a run that reads 97 of 100
    /// and reports Success is a different thing from one that read 97 of 97 — and only this number tells
    /// them apart.
    /// </summary>
    public long RowsRejected { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Hangfire job id when run in the background. Null for an inline run.</summary>
    [StringLength(100)]
    public string? JobId { get; set; }

    /// <summary>Captured progress lines, so a run can be diagnosed without the Hangfire dashboard.</summary>
    public string? Log { get; set; }

    /// <summary>machine:pid that claimed this run. With <see cref="HeartbeatAt"/>, tells a stale run from a slow one.</summary>
    [StringLength(64)]
    public string? RunnerId { get; set; }

    public DateTime? HeartbeatAt { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// One node execution within a run.
/// <para>
/// A table rather than a blob on the run, for three reasons: the waterfall renders from indexed columns
/// without downloading every step's preview; per-step row counts are queryable across runs; and a resume
/// or a diagnosis is a projection rather than a parse, so one malformed byte cannot discard a whole run's
/// history.
/// </para>
/// </summary>
public class PipelineRunStep
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string RunId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;

    [StringLength(450)]
    public string? PipelineId { get; set; }

    [Required]
    [StringLength(128)]
    public string NodeId { get; set; } = string.Empty;

    [StringLength(64)]
    public string? NodeType { get; set; }

    [StringLength(200)]
    public string? NodeLabel { get; set; }

    /// <summary>Execution order. The waterfall sorts on this rather than on timestamps.</summary>
    public int StepIndex { get; set; }

    public int Attempt { get; set; } = 1;

    [Required]
    [StringLength(16)]
    public string Status { get; set; } = PipelineStepStatus.Pending;

    public long? RowsIn { get; set; }
    public long? RowsOut { get; set; }

    /// <summary>Rows this step skipped as unparseable. Null for steps where the idea does not apply.</summary>
    public long? RowsRejected { get; set; }

    /// <summary>
    /// The SQL this step actually generated and ran. The single most useful thing in this table when
    /// something produced the wrong numbers — every transform is SQL underneath, and without this the only
    /// way to see it is to re-derive it by hand.
    /// </summary>
    public string? SqlText { get; set; }

    /// <summary>A few rows of this step's output as JSON, so the run view can show what it produced.</summary>
    public string? OutputPreviewJson { get; set; }

    /// <summary>The relation's columns as JSON, used to refresh the graph's schema cache after a run.</summary>
    public string? OutputColumnsJson { get; set; }

    public string? Error { get; set; }

    [StringLength(64)]
    public string? ErrorType { get; set; }

    public int DurationMs { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

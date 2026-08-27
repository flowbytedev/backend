namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// Operator-controlled limits for pipeline execution, bound from the <c>Pipelines</c> configuration
/// section. Every value has a working default, so a deployment that says nothing about pipelines still runs
/// them sensibly.
/// <para>
/// A pipeline's own settings may tighten these but never exceed them — a graph author must not be able to
/// raise a limit the operator set.
/// </para>
/// </summary>
public class PipelineOptions
{
    /// <summary>
    /// Where per-run scratch databases live. Defaults to a <c>_pipeline_runs</c> folder beside the dataset
    /// files, so it inherits whatever disk the datasets are already on.
    /// </summary>
    public string? ScratchDirectory { get; set; }

    /// <summary>Graph size ceiling, matching the compiler's own default.</summary>
    public int MaxNodes { get; set; } = 60;

    /// <summary>Rows returned by a preview / partial run.</summary>
    public int PreviewRows { get; set; } = 100;

    /// <summary>Rows stored on each step row so the run view can show what a step produced.</summary>
    public int StepPreviewRows { get; set; } = 20;

    /// <summary>
    /// Source fetches that run concurrently. Only the network-bound fetch is parallel — loading into DuckDB
    /// is always serial, because a DuckDB file takes one writer.
    /// </summary>
    public int SourceFetchConcurrency { get; set; } = 3;

    /// <summary>Rows per batch when writing out to an external database.</summary>
    public int ExternalWriteBatchSize { get; set; } = 10000;

    /// <summary>
    /// How long a failed run's scratch database is kept. Failures are where the intermediate tables are
    /// worth having, so they outlive the run rather than being cleaned up with it.
    /// </summary>
    public int RetainScratchOnFailureDays { get; set; } = 3;

    /// <summary>How long step rows are kept before the retention sweep hard-deletes them.</summary>
    public int StepRetentionDays { get; set; } = 30;

    /// <summary>
    /// A run whose heartbeat is older than this is considered abandoned and failed.
    /// <para>
    /// Generous on purpose. The engine heartbeats between steps, not during one, so the real window is "a
    /// single step took longer than this" — and in ETL a single step legitimately can: one fetch of a
    /// hundred million rows is not a hung runner. Set too low, healthy long-running loads get reported as
    /// abandoned. Two hours still cleans up after a genuine crash well within a day.
    /// </para>
    /// </summary>
    public int StaleRunMinutes { get; set; } = 120;

    /// <summary>
    /// Folders a <c>source.file</c> step is allowed to read from. Empty means no restriction, which is the
    /// right default for an on-premise deployment where the service account's own permissions are the
    /// boundary — but setting it is worthwhile, because a DATA_ADMIN can otherwise point a pipeline at any
    /// path the service account can read.
    /// </summary>
    public List<string> AllowedSourceDirectories { get; set; } = new();

    // ---- API source and destination ----

    /// <summary>Per-request deadline for an API step. Overridable per credential.</summary>
    public int ApiTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Attempts per request, including the first. Only rate limiting and 5xx are retried — a 4xx is a
    /// request this side got wrong, and sending it again just fails slower.
    /// </summary>
    public int ApiRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Hard ceiling on pages a single API source will fetch. This is a runaway guard, not a tuning knob: a
    /// cursor endpoint that keeps returning the same token, or a page counter the server ignores, would
    /// otherwise loop until the run timeout with the row count climbing the whole way.
    /// </summary>
    public int ApiMaxPages { get; set; } = 1000;

    /// <summary>Default page size when a paginated source does not set its own.</summary>
    public int ApiPageSize { get; set; } = 100;

    /// <summary>Rows per request for a batching API destination.</summary>
    public int ApiWriteBatchSize { get; set; } = 500;

    public string ResolveScratchDirectory(string duckdbFilePath) =>
        string.IsNullOrWhiteSpace(ScratchDirectory)
            ? System.IO.Path.Combine(duckdbFilePath, "_pipeline_runs")
            : ScratchDirectory!;

    public int ResolvePreviewRows() => Math.Clamp(PreviewRows, 1, 1000);
    public int ResolveStepPreviewRows() => Math.Clamp(StepPreviewRows, 0, 200);
    public int ResolveSourceFetchConcurrency() => Math.Clamp(SourceFetchConcurrency, 1, 8);
    public int ResolveMaxNodes() => Math.Clamp(MaxNodes, 1, 500);
    public int ResolveApiTimeoutSeconds() => Math.Clamp(ApiTimeoutSeconds, 1, 3600);
    public int ResolveApiRetryAttempts() => Math.Clamp(ApiRetryAttempts, 1, 10);
    public int ResolveApiMaxPages() => Math.Clamp(ApiMaxPages, 1, 100_000);
    public int ResolveApiPageSize() => Math.Clamp(ApiPageSize, 1, 100_000);
    public int ResolveApiWriteBatchSize() => Math.Clamp(ApiWriteBatchSize, 1, 100_000);
}

/// <summary>
/// The <c>AzureBlob</c> configuration section. It has existed in appsettings for a while with nothing
/// reading it; the pipeline blob file source is the first consumer.
/// </summary>
public class AzureBlobOption
{
    public string? ConnectionString { get; set; }

    /// <summary>Container used when a step does not name one.</summary>
    public string? ContainerName { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}

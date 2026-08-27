namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// Run states. Strings rather than an enum, matching <c>IngestionRun.Status</c> — the processor claims a
/// run with a single <c>ExecuteUpdateAsync(… SetProperty(r =&gt; r.Status, Running))</c> guarded on the
/// current value, which an enum-with-conversion cannot express, and the column stays readable in SQL.
/// <para>Keep every value at or under 16 characters: the column is NVARCHAR(16).</para>
/// </summary>
public static class PipelineRunStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";

    /// <summary>Nothing will change this run's status again — stop polling, stop requeueing.</summary>
    public static bool IsTerminal(string? status) => status is Success or Failed or Canceled;
}

/// <summary>Per-node states within a run.</summary>
public static class PipelineStepStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Success = "Success";
    public const string Failed = "Failed";

    /// <summary>
    /// Never ran because something upstream failed. Distinct from Failed on purpose: a skipped node is
    /// not a second bug to investigate, and the run inspector should not present it as one.
    /// </summary>
    public const string Skipped = "Skipped";
}

/// <summary>How a run was started. Stored on the run so history explains itself without a join.</summary>
public static class PipelineTriggerType
{
    public const string Manual = "Manual";
    public const string Cron = "Cron";
    public const string Api = "Api";

    /// <summary>A preview/partial execution — never writes to a destination.</summary>
    public const string Preview = "Preview";
}

/// <summary>
/// Machine-readable failure causes, stored alongside the human message so the run inspector can style a
/// budget/lock/drift failure differently from a generic one, and so support can group failures.
/// </summary>
public static class PipelineErrorType
{
    /// <summary>A mapped source column no longer exists upstream. The schema-drift case.</summary>
    public const string SchemaDrift = "schema_drift";

    /// <summary>Could not take the DuckDB file lock — another process or a data-viewer tab holds it.</summary>
    public const string DatasetBusy = "dataset_busy";

    /// <summary>The destination's connection has no write rights.</summary>
    public const string NotWritable = "not_writable";

    /// <summary>Could not reach or authenticate to a source.</summary>
    public const string SourceUnavailable = "source_unavailable";

    /// <summary>A source produced no rows and the pipeline was configured to treat that as an error.</summary>
    public const string EmptySource = "empty_source";

    /// <summary>The generated SQL was rejected by DuckDB — usually a bad cast or expression.</summary>
    public const string SqlError = "sql_error";

    /// <summary>Exceeded the run-level timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>The graph failed validation at run time (a stored graph edited into an invalid state).</summary>
    public const string Invalid = "invalid";

    /// <summary>Too many rows could not be parsed — over the step's configured limit.</summary>
    public const string BadRows = "bad_rows";

    /// <summary>
    /// An HTTP API refused, failed, or could not be reached. Distinct from <see cref="SourceUnavailable"/>
    /// because an API destination fails this way too, and distinct from <see cref="NotWritable"/> because
    /// that one means "the credential is not allowed to write", not "the server said no".
    /// </summary>
    public const string ApiError = "api_error";

    public const string Canceled = "canceled";
    public const string Unknown = "unknown";
}

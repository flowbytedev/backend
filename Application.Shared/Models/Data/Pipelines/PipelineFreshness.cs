using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// How recently a step must have produced its output before somebody should be told. Declared on the graph
/// document — either as <see cref="PipelineSettings.Freshness"/> for every node at once, or on a single
/// <see cref="PipelineNodeDef.Freshness"/> to override it.
/// <para>
/// <b>Why a policy rather than a fixed rule.</b> "Late" has no universal definition here: a feed that lands
/// hourly is broken after ninety minutes, and a monthly reconciliation is not. Only the person who built the
/// graph knows which, so the threshold is authored alongside it rather than inferred from run history — an
/// inferred one learns the outage as the new normal.
/// </para>
/// <para>
/// The two modes are not interchangeable. <see cref="MaxLagMinutes"/> asks "how long since this last
/// worked", which suits a continuously-fed step. <see cref="Cron"/> asks "did it work since it was
/// supposed to", which is the only one that expresses a business deadline — a report due at 6am is late at
/// 6:05 whether the last run was twenty minutes or twenty hours ago. Expressing that as a lag means picking
/// a number that drifts wrong every time the schedule changes.
/// </para>
/// </summary>
public sealed class PipelineFreshnessPolicy
{
    /// <summary>
    /// Set false on a node to opt it out of an inherited pipeline-wide policy. Absent this, "no policy on
    /// this node" and "inherit the default" would be the same document, and a node could never be excluded
    /// once a default existed.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum minutes since this step last succeeded. Mutually exclusive with <see cref="Cron"/>.
    /// </summary>
    public int? MaxLagMinutes { get; set; }

    /// <summary>
    /// Five- or six-field cron naming the deadline: the step must have succeeded since the most recent
    /// occurrence. Evaluated in <see cref="TimeZone"/>. Mutually exclusive with
    /// <see cref="MaxLagMinutes"/>.
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>
    /// Minutes of slack after a <see cref="Cron"/> deadline before the step is called late. A run takes
    /// time; without grace, a deadline of 6am reports every pipeline that starts at 6am as stale for as
    /// long as it runs.
    /// </summary>
    public int GraceMinutes { get; set; } = 15;

    /// <summary>
    /// Zone the <see cref="Cron"/> deadline is read in. Null follows the pipeline's own schedule zone,
    /// which is nearly always what was meant — a deadline and the schedule meant to meet it disagreeing
    /// about midnight is a bug nobody finds until the clocks change.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>True when this policy actually asserts something.</summary>
    public bool IsActive => Enabled && (MaxLagMinutes is > 0 || !string.IsNullOrWhiteSpace(Cron));

    /// <summary>
    /// Why this policy cannot be evaluated, or null when it is fine. Returned rather than thrown so the
    /// validator can surface it beside the other graph issues.
    /// </summary>
    public string? Validate()
    {
        var hasLag = MaxLagMinutes is not null;
        var hasCron = !string.IsNullOrWhiteSpace(Cron);

        if (hasLag && hasCron)
            return "A freshness policy sets either maxLagMinutes or cron, not both — they answer different "
                 + "questions and would disagree.";

        if (hasLag && MaxLagMinutes <= 0)
            return "maxLagMinutes must be greater than zero.";

        if (GraceMinutes < 0)
            return "graceMinutes cannot be negative.";

        if (hasCron && !CronFields.LooksParseable(Cron!))
            return $"'{Cron}' is not a schedule this server can read. Use five fields, for example "
                 + "'0 6 * * *' for 6am daily.";

        if (TimeZone is not null && !ScheduleTimeZones.IsKnown(TimeZone))
            return $"'{TimeZone}' is not a time zone this server knows.";

        return null;
    }
}

/// <summary>Freshness verdict for one node. The strings are a wire contract — the client styles on them.</summary>
public static class PipelineFreshnessStatus
{
    /// <summary>No active policy covers this node, so nothing is asserted about it.</summary>
    public const string Unchecked = "Unchecked";

    /// <summary>Produced its output within the policy.</summary>
    public const string Fresh = "Fresh";

    /// <summary>Has produced output before, but not recently enough.</summary>
    public const string Stale = "Stale";

    /// <summary>
    /// Has never produced output under this policy — a distinct state from <see cref="Stale"/> on purpose.
    /// A node that has never run is usually a new node or a broken schedule, not a late one, and the two
    /// want different responses.
    /// </summary>
    public const string Never = "Never";

    /// <summary>
    /// A policy that cannot be evaluated — an unreadable cron, or a zone this host has no data for.
    /// Reported rather than treated as fresh: a check that silently stops checking is worse than no check.
    /// </summary>
    public const string Unknown = "Unknown";

    /// <summary>True for the two states that mean somebody should look.</summary>
    public static bool IsViolation(string? status) => status is Stale or Never;

    /// <summary>Rank for rolling per-node verdicts up to one pipeline-level status. Higher wins.</summary>
    public static int Rank(string? status) => status switch
    {
        Stale => 4,
        Never => 3,
        Unknown => 2,
        Fresh => 1,
        _ => 0
    };
}

/// <summary>
/// When each step last produced its output, surviving the step rows it was derived from.
/// <para>
/// <b>Why this is stored rather than queried.</b> "Last successful materialization" is visible in
/// <c>pipeline_run_step</c> — until <c>PurgeOldStepsAsync</c> hard-deletes it at
/// <see cref="PipelineOptions.StepRetentionDays"/>. A node that last ran forty days ago would then have no
/// step row at all, and a freshness check computed from steps would read that as "never ran" — reporting
/// the most stale nodes in the system as new ones. The whole point of the feature is to notice long
/// silences, so it cannot be built on telemetry that is deleted for being old.
/// </para>
/// <para>
/// Keyed on (pipeline, node) like <see cref="PipelineState"/>, and for the same reason: the answer is
/// per-step, and one row per pipeline would make every node inherit whichever one ran last.
/// </para>
/// </summary>
public class PipelineNodeFreshness : BaseModel
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [MaxLength(450)]
    public string PipelineId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// When this step last ended in Success. Null means it has never succeeded since this table started
    /// being written, which for an existing pipeline is not the same as "never ran" — see
    /// <c>docs/PIPELINES.md</c> on the backfill gap.
    /// </summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>The run that last succeeded here, for tracing back to the step's SQL while it still exists.</summary>
    [MaxLength(450)]
    public string? LastSuccessRunId { get; set; }

    /// <summary>Rows the step produced on that run. Diagnostic: fresh but suddenly zero is its own problem.</summary>
    public long? LastRowsOut { get; set; }

    // ---- Alert state. Only the sweep writes these. ------------------------------------------------

    /// <summary>
    /// Verdict recorded by the last sweep, so a transition can be detected. Without it the sweep cannot
    /// tell "went stale just now" from "has been stale for a week", and would re-alert on every pass.
    /// </summary>
    [MaxLength(20)]
    public string? AlertedStatus { get; set; }

    /// <summary>When the sweep last changed <see cref="AlertedStatus"/>.</summary>
    public DateTime? AlertedAt { get; set; }
}

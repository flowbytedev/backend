using Application.Shared.Models.Data.Pipelines;

namespace Application.Client.Pipelines;

/// <summary>
/// Per-step execution state as the canvas needs it. Built from the poll DTO's step ticks, so the same
/// <c>PipelineCanvas</c> renders the editor (no run state) and the run view (run state supplied). The
/// renderer is never forked — otherwise the two views drift and the "this is the same graph" illusion breaks
/// the first time someone flips between them.
/// </summary>
public sealed record NodeRunState(
    string Status,
    int DurationMs,
    long? RowsOut,
    string? Error,
    /// <summary>Offset from the run's start, used only by the waterfall to position a bar.</summary>
    int StepIndex,
    /// <summary>Rows the step expects in total, when it knows. Null for a step that cannot.</summary>
    long? RowsTotal = null)
{
    public bool IsTerminal => PipelineStepStatus.IsTerminal(Status);

    /// <summary>
    /// How far through this step is, 0-100, or null when there is nothing honest to draw.
    /// <para>
    /// Only while <b>running</b>: a finished step's bar is noise, and one that failed part-way through
    /// would show a bar frozen at 60% next to an error, which reads as "still going". Clamped, because the
    /// total is the upstream step's row count and a filter in between can make the real total smaller.
    /// </para>
    /// </summary>
    public int? Percent =>
        Status == PipelineStepStatus.Running && RowsTotal is > 0 && RowsOut is >= 0
            ? (int)Math.Min(100, RowsOut.Value * 100 / RowsTotal.Value)
            : null;

    /// <summary>Folds a run's step ticks into one state per step.</summary>
    public static Dictionary<string, NodeRunState> FromTicks(IEnumerable<PipelineStepTickDto>? ticks)
    {
        var result = new Dictionary<string, NodeRunState>(StringComparer.Ordinal);
        if (ticks is null) return result;

        foreach (var tick in ticks)
        {
            // A later step index for the same node is a retry, and the latest outcome is the one to show.
            if (result.TryGetValue(tick.NodeId, out var existing) && existing.StepIndex > tick.StepIndex)
                continue;

            result[tick.NodeId] = new NodeRunState(
                tick.Status, tick.DurationMs, tick.RowsOut, tick.Error, tick.StepIndex, tick.RowsTotal);
        }

        return result;
    }

    /// <summary>Merges a delta poll into an existing map, in place, returning true when anything changed.</summary>
    public static bool Merge(Dictionary<string, NodeRunState> into, IEnumerable<PipelineStepTickDto>? ticks)
    {
        var changed = false;
        if (ticks is null) return false;

        foreach (var (nodeId, state) in FromTicks(ticks))
        {
            if (into.TryGetValue(nodeId, out var existing) && existing == state) continue;
            into[nodeId] = state;
            changed = true;
        }

        return changed;
    }

    /// <summary>Builds the map from a full step list, for opening a finished run.</summary>
    public static Dictionary<string, NodeRunState> FromSteps(IEnumerable<PipelineRunStepDto>? steps)
    {
        var result = new Dictionary<string, NodeRunState>(StringComparer.Ordinal);
        if (steps is null) return result;

        foreach (var step in steps.OrderBy(s => s.StepIndex))
            result[step.NodeId] = new NodeRunState(
                step.Status, step.DurationMs, step.RowsOut, step.Error, step.StepIndex);

        return result;
    }
}

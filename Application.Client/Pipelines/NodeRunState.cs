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
    int StepIndex)
{
    public bool IsTerminal => Status is PipelineStepStatus.Success
                                     or PipelineStepStatus.Failed
                                     or PipelineStepStatus.Skipped;

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
                tick.Status, tick.DurationMs, tick.RowsOut, tick.Error, tick.StepIndex);
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

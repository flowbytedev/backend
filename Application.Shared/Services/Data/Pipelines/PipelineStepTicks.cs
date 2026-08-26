using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Cursor and revision arithmetic for the run-status poll.
/// <para>
/// Pulled out of the query so it can be tested without a database. Both rules below were got wrong the
/// first time and neither failure is visible in a screenshot — the run simply looks stuck, which is
/// indistinguishable from a run that genuinely is.
/// </para>
/// </summary>
public static class PipelineStepTicks
{
    /// <summary>
    /// Where the client's cursor should sit after this batch.
    /// <para>
    /// Only a <b>settled</b> step advances it. A running step is re-sent every poll so its row count can
    /// climb; if it also moved the cursor, then the moment it finished it would satisfy neither
    /// <c>StepIndex &gt; since</c> nor <c>Status == Running</c> — and its final row count, duration and
    /// success would never be delivered. The node would sit showing a spinner forever.
    /// </para>
    /// </summary>
    public static int NextCursor(IReadOnlyList<PipelineStepTickDto> ticks, int since)
    {
        var settled = since;

        foreach (var tick in ticks)
            if (tick.Status != PipelineStepStatus.Running && tick.StepIndex > settled)
                settled = tick.StepIndex;

        return settled;
    }

    /// <summary>
    /// A revision that only ever grows, so the client can discard a stale response.
    /// <para>
    /// Deliberately excludes the number of ticks in the batch. That count RISES as steps accumulate and
    /// FALLS again once the cursor advances past them, which made the revision go backwards — and the
    /// client treats a lower revision as out-of-order and drops the whole response. A long run would stop
    /// updating partway through and look frozen.
    /// </para>
    /// <para>
    /// A running step's changing row count does not move this, and does not need to: the client merges
    /// whatever steps arrive regardless of the revision. This exists only to order responses.
    /// </para>
    /// </summary>
    public static int Revision(int completed, int failed, int skipped) => completed + failed + skipped;
}

namespace Application.Scheduler.Jobs;

/// <summary>
/// The shared rule for the registrar jobs' orphan sweep: which recurring job ids a registrar owns, and may
/// therefore delete when its table no longer has a row for them.
/// <para>
/// A prefix test alone is not that rule. <c>pipeline-registrar</c> and <c>pipeline-maintenance</c> both
/// start with the pipeline registrar's <c>pipeline-</c> prefix, so a plain <c>StartsWith</c> had it delete
/// itself and the maintenance job on its first pass — after which nothing reconciled at all, and no
/// schedule created since the last scheduler start ever reached Hangfire. <c>notebook-run-registrar</c> sat
/// inside its own registrar's <c>notebook-run-</c> prefix the same way.
/// </para>
/// <para>
/// Every registrar names its jobs <c>{prefix}{entity id}</c> and every entity id is a GUID, so requiring
/// the suffix to parse as one separates the jobs a registrar created from the fixed infrastructure ids
/// registered in <c>Program.cs</c>. It fails in the safe direction too: an id that stops looking like ours
/// is left alone rather than removed, so the worst case is a stale recurring job rather than a scheduler
/// that quietly stops scheduling.
/// </para>
/// </summary>
internal static class RegistrarSweep
{
    /// <summary>Whether this recurring job is one the registrar owning <paramref name="prefix"/> created.</summary>
    public static bool IsOwned(string recurringJobId, string prefix) =>
        recurringJobId.StartsWith(prefix, StringComparison.Ordinal)
        && Guid.TryParse(recurringJobId.AsSpan(prefix.Length), out _);
}

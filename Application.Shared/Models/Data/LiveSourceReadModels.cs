namespace Application.Shared.Models.Data;

/// <summary>
/// Whether a read may be served from a dataset's live external source, and if not, why. Every non-
/// <see cref="Allowed"/> value is a refusal the caller can act on — the API-key data endpoints map each to
/// its own status code rather than collapsing them, because they are different people's problems.
/// </summary>
public enum LiveSourceReadOutcome
{
    /// <summary>The read may proceed against the source connection.</summary>
    Allowed,

    /// <summary>No dataset with that id in the API key's company.</summary>
    DatasetNotFound,

    /// <summary>The dataset is Local, or External with no source connection saved — there is no live layer to read.</summary>
    NotExternalSource,

    /// <summary>
    /// An acting user was named who carries column grants or row-level-security filters. Those cannot be
    /// enforced against a live source, so the read is refused instead of served unsecured. Same fail-closed
    /// posture (and same reason) as <c>security_not_enforceable</c> on <c>api/dataset/{id}/query/run</c>:
    /// a live table is <c>{schema}.{name}</c>, which a CTE can neither be named nor shadow, so the secured-
    /// relation rewrite that enforces masking and RLS has nothing to bind to.
    /// </summary>
    SecurityNotEnforceable,

    /// <summary>An acting user was named who has no access to this dataset at all.</summary>
    UserNotPermitted
}

/// <summary>
/// Resolved permission to read a dataset's live external source, plus the constraints that apply.
/// Produced by <c>IDatasetLiveSourceService.ResolveReadAsync</c>.
/// </summary>
public class LiveSourceRead
{
    public LiveSourceReadOutcome Outcome { get; init; }

    public bool Allowed => Outcome == LiveSourceReadOutcome.Allowed;

    /// <summary>
    /// The Database-type asset whose saved connection serves the read (<see cref="Models.Dataset.SourceEntityId"/>).
    /// Set only when <see cref="Allowed"/>.
    /// </summary>
    public string? SourceEntityId { get; init; }

    /// <summary>
    /// Tables the acting user may read, or <c>null</c> for "all tables" — the same convention as
    /// <c>IDatasetService.GetAccessibleTablesAsync</c>. Always null when no acting user was supplied, in
    /// which case the API key's own scope is the only table control (see the service remarks).
    /// </summary>
    public HashSet<string>? AllowedTables { get; init; }

    /// <summary>Human-readable reason for a refusal. Null when <see cref="Allowed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>True when <paramref name="tableName"/> is inside the acting user's table scope.</summary>
    public bool MayReadTable(string tableName) =>
        AllowedTables == null || AllowedTables.Contains(tableName);
}

namespace Application.Shared.Enums;

/// <summary>
/// How per-user column masking and row-level security are enforced for one external source.
/// </summary>
/// <remarks>
/// A dataset's <b>snapshot</b> layer is always enforced by SQL rewriting against DuckDB and is not
/// covered by this enum — it needs no decision, because the rewrite provably works there (a CTE named
/// <c>orders</c> shadows the table <c>orders</c>, and qualified references are refused outright, so
/// there is exactly one way to name a table and the secured relation always intercepts it).
/// <para>
/// The live path is the one that needs a choice, because a live source's tables are
/// <c>{schema}.{name}</c>: a CTE can neither be named that nor shadow it. Either the source enforces
/// the rules itself (<see cref="Native"/>), or Relay rewrites each reference into a secured subquery
/// and refuses anything it cannot prove it rewrote (<see cref="Rewrite"/>).
/// </para>
/// </remarks>
public enum RlsEnforcementMode
{
    /// <summary>
    /// No choice recorded yet. Restricted users are refused on the live path — the same fail-closed
    /// posture as before a decision existed. Never treat this as "no restrictions apply".
    /// </summary>
    Undecided = 0,

    /// <summary>
    /// The source enforces it: roles/policies and column grants are provisioned into the source database,
    /// and queries run under an identity holding only that user's permissions. Strongest option — the
    /// engine is the boundary, so no SQL analysis is trusted — but it needs a credential that can create
    /// those objects.
    /// </summary>
    Native = 1,

    /// <summary>
    /// Relay rewrites the SQL: every referenced table becomes a subquery projecting only granted columns
    /// and applying the row filter, and the query is refused if any route to a base table survives the
    /// rewrite. Needs no privileges on the source, and is the only option for engines with no native
    /// row-level security (DuckDB, MySQL). Weaker than <see cref="Native"/>: it relies on finding every
    /// table reference, and the SQL is model-written from user-influenced prompt text.
    /// </summary>
    Rewrite = 2
}

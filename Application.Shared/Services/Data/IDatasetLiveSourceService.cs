using Application.Shared.Models.Data;

namespace Application.Shared.Services.Data;

/// <summary>
/// Decides whether a read may be served from a dataset's <b>live external source</b> instead of its local
/// DuckDB snapshot, and hands back the source connection plus the acting user's table scope. Backs the
/// <c>?source=live</c> option on the API-key data endpoints (<c>api/external/datasets/*</c>).
/// </summary>
/// <remarks>
/// Snapshot reads need none of this — they go straight to DuckDB, and the API key's per-table scope is the
/// whole control. The live path gets its own gate for two reasons:
/// <list type="bullet">
/// <item>An External dataset's DuckDB file holds only saved snapshots, so "live" and "snapshot" are
/// genuinely different data; asking for one and getting the other silently is worse than an error.</item>
/// <item>Column masking and row-level security <b>cannot</b> be enforced against a live source (see
/// <see cref="LiveSourceReadOutcome.SecurityNotEnforceable"/>), so a restricted acting user has to be
/// refused rather than served unsecured rows.</item>
/// </list>
/// <para>
/// <b>The acting user is optional here, and that is deliberate.</b> <c>api/external/*</c> authenticates the
/// integration, not a human: the key carries the company and its <c>ApiKeyScope</c> rows carry the readable
/// tables. When the caller sends no <c>X-User-Id</c> there is no user whose grants could be applied, and the
/// key's scope remains the only table control — exactly as on the existing snapshot endpoints. Sending a
/// user does not widen access, only narrow it (or refuse). A caller that needs per-user column masking and
/// RLS actually applied must use <c>POST api/dataset/{id}/query/run</c>, which is the only row-returning
/// path that enforces them (see <c>docs/PUBLIC-SQL-QUERY-API.md</c>).
/// </para>
/// </remarks>
public interface IDatasetLiveSourceService
{
    /// <summary>
    /// Resolves a live-source read for <paramref name="datasetId"/> within <paramref name="companyId"/>.
    /// Pass <paramref name="actingUserId"/> when the caller identified an end user (<c>X-User-Id</c>);
    /// pass null when it did not. Never throws for a refusal — inspect
    /// <see cref="LiveSourceRead.Outcome"/>.
    /// </summary>
    Task<LiveSourceRead> ResolveReadAsync(string companyId, string datasetId, string? actingUserId,
        CancellationToken ct = default);
}

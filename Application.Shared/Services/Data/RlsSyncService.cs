using Application.Shared.Enums;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services.Data;

/// <summary>
/// Pushes this app's grant tables into a source that enforces them itself, so the roles and policies out
/// there track what an operator changes in here.
/// </summary>
/// <remarks>
/// <b>A failed push never fails the local save.</b> The grant tables are the record of intent and an
/// operator must be able to administer access whether or not the source is reachable. That is only safe
/// because the query path verifies before every query
/// (<see cref="IRlsProvisioner.VerifyAsync"/>): a push that did not land makes queries <i>refuse</i>,
/// not leak. Failing the save instead would trade a fail-closed outcome for an unusable admin screen.
/// <para>
/// No-ops entirely unless the source is in <see cref="RlsEnforcementMode.Native"/> — a rewriting source
/// has nothing provisioned, and a source with no decision refuses restricted users anyway.
/// </para>
/// </remarks>
public interface IRlsSyncService
{
    /// <summary>Brings one acting user's role and policies in line with the grant tables.</summary>
    Task SyncUserAsync(string companyId, string datasetId, string userId, CancellationToken ct = default);

    /// <summary>Removes one acting user's role and policies from the source.</summary>
    Task RemoveUserAsync(string companyId, string datasetId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Re-syncs everyone on a source. Used when a source is switched to Native, where nothing has been
    /// provisioned yet, and by reconciliation.
    /// </summary>
    Task SyncSourceAsync(string companyId, string sourceEntityId, CancellationToken ct = default);
}

public class RlsSyncService : IRlsSyncService
{
    private readonly IRlsPlanBuilder _plans;
    private readonly IRlsModeService _modes;
    private readonly IRlsProvisioner _provisioner;
    private readonly ILogger<RlsSyncService> _logger;

    public RlsSyncService(
        IRlsPlanBuilder plans,
        IRlsModeService modes,
        IRlsProvisioner provisioner,
        ILogger<RlsSyncService> logger)
    {
        _plans = plans;
        _modes = modes;
        _provisioner = provisioner;
        _logger = logger;
    }

    public async Task SyncUserAsync(string companyId, string datasetId, string userId,
        CancellationToken ct = default)
    {
        var plan = await _plans.BuildAsync(companyId, datasetId, userId, ct);
        if (plan is null) return; // not external, or not visible to this user

        if (await _modes.GetModeAsync(plan.SourceEntityId, companyId, ct) != RlsEnforcementMode.Native)
            return;

        try
        {
            await _provisioner.ApplyAsync(plan, ct);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed — see the remarks on IRlsSyncService. Logged at Error because a
            // source drifting from the grant tables means restricted users are being refused, and an
            // operator needs to see why.
            _logger.LogError(ex,
                "Could not push row/column security for user {User} on dataset {Dataset} to source {Source}. "
                + "Queries for this user will be refused until it succeeds.",
                userId, datasetId, plan.SourceEntityId);
        }
    }

    public async Task RemoveUserAsync(string companyId, string datasetId, string userId,
        CancellationToken ct = default)
    {
        // The plan is gone once access is revoked, so the source id comes from the dataset rather than
        // from a plan we can no longer build.
        var plan = await _plans.BuildAsync(companyId, datasetId, userId, ct);
        var sourceEntityId = plan?.SourceEntityId;

        if (string.IsNullOrWhiteSpace(sourceEntityId)) return;
        if (await _modes.GetModeAsync(sourceEntityId, companyId, ct) != RlsEnforcementMode.Native) return;

        try
        {
            await _provisioner.RemoveAsync(companyId, sourceEntityId, datasetId, userId, ct);
        }
        catch (Exception ex)
        {
            // Worth escalating over a failed apply: a role left behind still grants access. The verify
            // step cannot save us here, because the person no longer has records to verify against.
            _logger.LogError(ex,
                "Could not remove row/column security for user {User} on dataset {Dataset} from source "
                + "{Source}. Their role may still exist there and should be dropped.",
                userId, datasetId, sourceEntityId);
        }
    }

    public async Task SyncSourceAsync(string companyId, string sourceEntityId,
        CancellationToken ct = default)
    {
        if (await _modes.GetModeAsync(sourceEntityId, companyId, ct) != RlsEnforcementMode.Native) return;

        try
        {
            await _provisioner.EnsureQueryIdentityAsync(companyId, sourceEntityId, ct);
        }
        catch (Exception ex)
        {
            // Without the query account nothing else can work, so stop rather than emit a run of
            // per-user failures that all have the same cause.
            _logger.LogError(ex,
                "Could not provision the query account on source {Source}; skipping the per-user sync.",
                sourceEntityId);
            return;
        }

        foreach (var (datasetId, userId) in await _plans.ListGrantedPairsAsync(companyId, sourceEntityId, ct))
            await SyncUserAsync(companyId, datasetId, userId, ct);
    }
}

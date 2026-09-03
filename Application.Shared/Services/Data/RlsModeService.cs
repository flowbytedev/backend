using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

/// <summary>
/// Reads and records how one external source enforces column masking and RLS on its live path.
/// </summary>
/// <remarks>
/// The decision is only ever put to an operator when there is a real choice. Two of the three outcomes
/// settle themselves:
/// <list type="bullet">
/// <item>The engine has no per-user row security (DuckDB, MySQL) → <see cref="RlsEnforcementMode.Rewrite"/>,
/// because there is nothing to provision and no question worth asking.</item>
/// <item>A credential we already hold can provision → <see cref="RlsEnforcementMode.Native"/>, the
/// stronger option, chosen without a prompt.</item>
/// <item>The engine could but the credential cannot → left <see cref="RlsEnforcementMode.Undecided"/>,
/// which refuses restricted users, until an operator either supplies a provisioning credential or
/// explicitly declines and accepts rewriting.</item>
/// </list>
/// Undecided deliberately refuses rather than silently rewriting: rewriting is the weaker enforcement,
/// and nobody should end up relying on it without having chosen it.
/// </remarks>
public interface IRlsModeService
{
    /// <summary>
    /// The effective mode for a source. <see cref="RlsEnforcementMode.Undecided"/> when nothing has been
    /// recorded — callers must treat that as "refuse", never as "unrestricted".
    /// </summary>
    Task<RlsEnforcementMode> GetModeAsync(string sourceEntityId, string companyId, CancellationToken ct = default);

    Task<RlsProvisioningModeDto?> GetAsync(string sourceEntityId, string companyId, CancellationToken ct = default);

    /// <summary>
    /// Stores a probe result, settling the mode automatically in the two cases that need no operator
    /// input. Returns the stored state so a caller can see whether a prompt is still required.
    /// </summary>
    Task<RlsProvisioningModeDto> RecordProbeAsync(string sourceEntityId, string companyId,
        RlsCapabilityReport report, CancellationToken ct = default);

    /// <summary>Records an operator's explicit choice, including a decline.</summary>
    Task<RlsProvisioningModeDto> DecideAsync(string sourceEntityId, string companyId,
        RlsModeDecisionRequest request, string? decidedBy, CancellationToken ct = default);
}

public class RlsModeService : IRlsModeService
{
    private readonly ApplicationDbContext _db;

    public RlsModeService(ApplicationDbContext db) => _db = db;

    public async Task<RlsEnforcementMode> GetModeAsync(string sourceEntityId, string companyId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceEntityId)) return RlsEnforcementMode.Undecided;

        var row = await _db.RlsProvisioningMode.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.SourceEntityId == sourceEntityId, ct);

        return row?.Mode ?? RlsEnforcementMode.Undecided;
    }

    public async Task<RlsProvisioningModeDto?> GetAsync(string sourceEntityId, string companyId,
        CancellationToken ct = default)
    {
        var row = await _db.RlsProvisioningMode.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.SourceEntityId == sourceEntityId, ct);

        return row == null ? null : ToDto(row);
    }

    public async Task<RlsProvisioningModeDto> RecordProbeAsync(string sourceEntityId, string companyId,
        RlsCapabilityReport report, CancellationToken ct = default)
    {
        var row = await LoadOrCreateAsync(sourceEntityId, companyId, ct);

        row.ProbedAt = DateTime.UtcNow;
        row.ProbeDetail = report.Detail;

        if (!report.EngineSupportsNativeRls)
        {
            // No question to ask: rewriting is the only enforcement this engine can have. Not recorded as
            // a decline, because nobody declined anything.
            row.Mode = RlsEnforcementMode.Rewrite;
        }
        else if (report.CurrentCredentialCanProvision)
        {
            // Deliberately NOT auto-selected. Choosing Native creates roles and policies inside someone's
            // production database, which is not a side effect a capability probe should trigger — and
            // while the provisioners are unimplemented it would strand the source on a mode that refuses
            // every restricted query. So capability is recorded and the operator picks.
            //
            // A credential that now works clears an earlier decline: the operator's "no" was to being
            // asked for a credential, and that obstacle is gone.
            row.ProvisioningDeclined = false;
            if (row.Mode == RlsEnforcementMode.Undecided) row.ProbeDetail = report.Detail;
        }
        else if (!row.ProvisioningDeclined)
        {
            // The engine could enforce this but the credential cannot, and nobody has declined yet — leave
            // it Undecided so the operator is prompted, and so restricted users stay refused until they
            // answer. Overwriting an existing Native here would silently weaken a working source.
            if (row.Mode != RlsEnforcementMode.Native) row.Mode = RlsEnforcementMode.Undecided;
        }

        row.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task<RlsProvisioningModeDto> DecideAsync(string sourceEntityId, string companyId,
        RlsModeDecisionRequest request, string? decidedBy, CancellationToken ct = default)
    {
        var row = await LoadOrCreateAsync(sourceEntityId, companyId, ct);

        row.Mode = request.Mode;
        // A decline is only meaningful alongside rewriting; recording it against Native would leave a
        // contradictory row that a later probe would have to guess at.
        row.ProvisioningDeclined = request.Declined && request.Mode == RlsEnforcementMode.Rewrite;
        row.DecidedBy = decidedBy;
        row.DecidedAt = DateTime.UtcNow;
        row.ModifiedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    private async Task<RlsProvisioningMode> LoadOrCreateAsync(string sourceEntityId, string companyId,
        CancellationToken ct)
    {
        var row = await _db.RlsProvisioningMode
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.SourceEntityId == sourceEntityId, ct);

        if (row != null) return row;

        row = new RlsProvisioningMode
        {
            CompanyId = companyId,
            SourceEntityId = sourceEntityId,
            Mode = RlsEnforcementMode.Undecided,
            CreatedAt = DateTime.UtcNow
        };
        _db.RlsProvisioningMode.Add(row);
        return row;
    }

    private static RlsProvisioningModeDto ToDto(RlsProvisioningMode m) => new()
    {
        SourceEntityId = m.SourceEntityId,
        Mode = m.Mode,
        ProvisioningDeclined = m.ProvisioningDeclined,
        ProbedAt = m.ProbedAt,
        ProbeDetail = m.ProbeDetail,
        DecidedBy = m.DecidedBy,
        DecidedAt = m.DecidedAt
    };
}

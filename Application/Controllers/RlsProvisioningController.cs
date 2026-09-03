using System.Security.Claims;
using Application.Shared.Authorization;
using Application.Shared.Enums;
using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// How one external source enforces per-user column masking and row-level security on its <b>live</b>
/// path: probe what the source is capable of, and record the operator's choice.
/// </summary>
/// <remarks>
/// Guarded by the same policy as database user provisioning, because that is what a "native" answer
/// leads to — creating roles and policies inside someone's production database is database
/// administration, not dataset sharing, and should not be reachable by anyone who can merely share a
/// dataset.
/// <para>
/// Nothing here decides access for a query. It records a decision; <c>PublicSqlQueryService</c> reads it
/// and refuses while it is <see cref="RlsEnforcementMode.Undecided"/>.
/// </para>
/// </remarks>
[Route("api/rls-provisioning")]
[ApiController]
[Authorize(Policy = PolicyNames.DatabaseAdmin)]
public class RlsProvisioningController : ControllerBase
{
    private readonly IRlsCapabilityProbe _probe;
    private readonly IRlsModeService _modes;
    private readonly IRlsSyncService _sync;
    private readonly IRlsPlanBuilder _plans;
    private readonly IRlsProvisioner _provisioner;

    public RlsProvisioningController(
        IRlsCapabilityProbe probe,
        IRlsModeService modes,
        IRlsSyncService sync,
        IRlsPlanBuilder plans,
        IRlsProvisioner provisioner)
    {
        _probe = probe;
        _modes = modes;
        _sync = sync;
        _plans = plans;
        _provisioner = provisioner;
    }

    /// <summary>
    /// Everything set up for one acting user on one dataset: this app's grants, the objects at the source,
    /// and whether the two agree.
    /// </summary>
    /// <remarks>
    /// Opened up to the data-admin policy rather than the database-admin one that guards the rest of this
    /// controller. Whoever grants the access should be able to see what it produced — and unlike the probe
    /// and decision endpoints, this creates nothing and returns no credential, only object names.
    /// </remarks>
    [HttpGet("inspect")]
    [Authorize(Policy = PolicyNames.DataAdminAccess)]
    public async Task<ActionResult<RlsInspectionDto>> Inspect(
        [FromQuery] string datasetId,
        [FromQuery] string userId,
        [FromHeader(Name = "X-Company-Id")] string companyId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(companyId)) return BadRequest("X-Company-Id header is required");
        if (string.IsNullOrWhiteSpace(datasetId) || string.IsNullOrWhiteSpace(userId))
            return BadRequest("datasetId and userId are required.");

        var result = new RlsInspectionDto { DatasetId = datasetId, UserId = userId };

        var plan = await _plans.BuildAsync(companyId, datasetId, userId, ct);
        if (plan is null)
        {
            // Local dataset, or not visible to this user. Either way there is nothing provisioned to show:
            // a Local dataset's grants are applied by rewriting against its own snapshot.
            result.AppliesToLivePath = false;
            return Ok(result);
        }

        result.AppliesToLivePath = true;
        result.Mode = await _modes.GetModeAsync(plan.SourceEntityId, companyId, ct);

        foreach (var table in plan.Tables)
        {
            var dto = new RlsInspectionTableDto
            {
                TableName = table.CatalogTable,
                GrantedColumns = table.GrantedColumns?.ToList()
            };

            foreach (var filter in table.Filters)
            {
                // A legacy filter names no table, so no policy is ever created for it. Reported with a
                // null policy name rather than one that was never provisioned — otherwise it would show
                // as "missing at source" and look like a sync failure instead of a data-migration task.
                dto.Filters.Add(new RlsInspectionFilterDto
                {
                    ColumnName = filter.ColumnName,
                    AllowedValues = filter.AllowedValues.ToList(),
                    LegacyAllTables = filter.AppliesToAllTables,
                    PolicyName = result.Mode == RlsEnforcementMode.Native && !filter.AppliesToAllTables
                        ? RlsNaming.PolicyName(datasetId, userId, table.CatalogTable, filter.ColumnName)
                        : null
                });
            }

            result.Tables.Add(dto);
        }

        // Source objects exist only in Native mode; rewriting creates nothing out there.
        if (result.Mode == RlsEnforcementMode.Native)
        {
            await _provisioner.DescribeSourceStateAsync(plan, result, ct);
            var verification = await _provisioner.VerifyAsync(plan, ct);
            result.VerificationOk = verification.Ok;
            result.VerificationProblem = verification.Problem;
        }
        else
        {
            // Rewriting has nothing to verify ahead of time: correctness is decided per query, by the
            // rewrite plus its fail-closed sweep.
            result.VerificationOk = true;
        }

        return Ok(result);
    }

    /// <summary>The recorded decision for a source, or 404 when none has been made yet.</summary>
    [HttpGet("{sourceEntityId}")]
    public async Task<ActionResult<RlsProvisioningModeDto>> Get(string sourceEntityId,
        [FromHeader(Name = "X-Company-Id")] string companyId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(companyId)) return BadRequest("X-Company-Id header is required");

        var stored = await _modes.GetAsync(sourceEntityId, companyId, ct);
        return stored == null ? NotFound() : Ok(stored);
    }

    /// <summary>
    /// Probes the source and records what was found, settling the mode automatically where there is no
    /// real choice. Returns both the report (what to tell the operator) and the stored state (whether a
    /// prompt is still needed).
    /// </summary>
    [HttpPost("{sourceEntityId}/probe")]
    public async Task<ActionResult<RlsProbeResponse>> Probe(string sourceEntityId,
        [FromHeader(Name = "X-Company-Id")] string companyId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(companyId)) return BadRequest("X-Company-Id header is required");

        var report = await _probe.ProbeAsync(sourceEntityId, companyId, ct);
        if (report == null)
            return NotFound($"Entity '{sourceEntityId}' has no saved database connection.");

        var stored = await _modes.RecordProbeAsync(sourceEntityId, companyId, report, ct);
        return Ok(new RlsProbeResponse { Report = report, Stored = stored });
    }

    /// <summary>
    /// Records the operator's choice. Sending <see cref="RlsEnforcementMode.Rewrite"/> with
    /// <c>declined = true</c> is the "I will not supply a provisioning credential" answer.
    /// </summary>
    [HttpPost("{sourceEntityId}/decision")]
    public async Task<ActionResult<RlsProvisioningModeDto>> Decide(string sourceEntityId,
        [FromBody] RlsModeDecisionRequest request,
        [FromHeader(Name = "X-Company-Id")] string companyId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(companyId)) return BadRequest("X-Company-Id header is required");

        // Undecided is a state the system starts in, not a choice anyone can make: accepting it here
        // would let an operator "decide" to leave restricted users refused, which the UI expresses by
        // simply not answering.
        if (request.Mode is not (RlsEnforcementMode.Native or RlsEnforcementMode.Rewrite))
            return BadRequest("Mode must be Native or Rewrite.");

        var decidedBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;
        var stored = await _modes.DecideAsync(sourceEntityId, companyId, request, decidedBy, ct);

        // Choosing Native means nothing is provisioned out there yet, so push everyone's grants now.
        // Recorded first: the mode is the record of intent, and the sync is best-effort — a source that
        // is unreachable right now leaves queries refusing (fail-closed) rather than losing the decision.
        if (stored.Mode == RlsEnforcementMode.Native)
            await _sync.SyncSourceAsync(companyId, sourceEntityId, ct);

        return Ok(stored);
    }
}

/// <summary>A probe result plus the decision state it produced.</summary>
public class RlsProbeResponse
{
    public RlsCapabilityReport Report { get; set; } = new();
    public RlsProvisioningModeDto Stored { get; set; } = new();
}

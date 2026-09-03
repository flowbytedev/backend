using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Application.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Models.Data;

/// <summary>
/// The recorded decision for how one external source enforces column masking and RLS on its live path.
/// </summary>
/// <remarks>
/// Keyed by <b>source entity</b>, not by dataset: several datasets can be backed by the same connected
/// database, and whether we can create policies there is a property of the source, not of each dataset
/// that reads it. Stored as a plain string with no FK — <c>entity</c> lives in the Status DbContext, the
/// same cross-context arrangement as <c>Dataset.SourceEntityId</c>.
/// </remarks>
[PrimaryKey(nameof(CompanyId), nameof(SourceEntityId))]
public class RlsProvisioningMode
{
    [Required] public string CompanyId { get; set; } = string.Empty;

    [Required][MaxLength(450)] public string SourceEntityId { get; set; } = string.Empty;

    public RlsEnforcementMode Mode { get; set; } = RlsEnforcementMode.Undecided;

    /// <summary>
    /// Whether an operator was offered a provisioning credential and turned it down. Kept distinct from
    /// <see cref="RlsEnforcementMode.Rewrite"/> so the UI can tell "chose rewriting" from "never asked",
    /// and so a later probe does not re-prompt someone who already said no.
    /// </summary>
    public bool ProvisioningDeclined { get; set; }

    /// <summary>When the source was last probed for provisioning capability.</summary>
    public DateTime? ProbedAt { get; set; }

    /// <summary>
    /// Human-readable outcome of the last probe, shown in the UI. Diagnostic only — never parsed to make
    /// an enforcement decision, which is what <see cref="Mode"/> is for.
    /// </summary>
    [MaxLength(2000)] public string? ProbeDetail { get; set; }

    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>What a probe found out about one source's ability to enforce RLS itself.</summary>
public class RlsCapabilityReport
{
    /// <summary>The engine probed.</summary>
    public DataSourceType Engine { get; set; }

    /// <summary>
    /// False when the engine has no per-user row security at all (DuckDB, MySQL). Rewriting is then the
    /// only option and there is nothing to offer a credential for.
    /// </summary>
    public bool EngineSupportsNativeRls { get; set; }

    /// <summary>True when the credential we already hold can create the required objects.</summary>
    public bool CurrentCredentialCanProvision { get; set; }

    /// <summary>
    /// True when the engine could do it but the credential cannot — the case where an operator should be
    /// offered the chance to supply a stronger user (or decline and fall back to rewriting).
    /// </summary>
    public bool NeedsStrongerCredential =>
        EngineSupportsNativeRls && !CurrentCredentialCanProvision;

    /// <summary>
    /// Whether this app can actually provision the engine, as opposed to the engine merely having the
    /// feature. Distinct from <see cref="EngineSupportsNativeRls"/> so the UI can tell an operator
    /// "your source could do this, but we haven't built it for this engine" instead of offering a
    /// choice that would leave every restricted query refused.
    /// </summary>
    public bool NativeProvisioningImplemented { get; set; }

    /// <summary>Which credential the probe tested, for the message shown to the operator.</summary>
    public string? TestedUsername { get; set; }

    /// <summary>Why provisioning is unavailable, when it is. Empty on success.</summary>
    public string? Detail { get; set; }

    /// <summary>The mode this report implies if the operator makes no further choice.</summary>
    public RlsEnforcementMode SuggestedMode =>
        CurrentCredentialCanProvision ? RlsEnforcementMode.Native : RlsEnforcementMode.Rewrite;
}

/// <summary>Payload recording an operator's enforcement choice for a source.</summary>
public class RlsModeDecisionRequest
{
    public RlsEnforcementMode Mode { get; set; }

    /// <summary>Set when the operator was offered a provisioning credential and declined it.</summary>
    public bool Declined { get; set; }
}

/// <summary>The stored decision as the browser sees it.</summary>
public class RlsProvisioningModeDto
{
    public string SourceEntityId { get; set; } = string.Empty;
    public RlsEnforcementMode Mode { get; set; }
    public bool ProvisioningDeclined { get; set; }
    public DateTime? ProbedAt { get; set; }
    public string? ProbeDetail { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
}

/// <summary>
/// The unprivileged identity that runs end-user queries against one external source in Native mode.
/// </summary>
/// <remarks>
/// Separate from the provisioning credential on purpose. Privileges on ClickHouse and PostgreSQL are the
/// union of a principal's own grants and its enabled roles, so naming a restricted role on a privileged
/// connection restricts nothing. Queries have to run as a principal with no grants of its own.
/// </remarks>
[PrimaryKey(nameof(CompanyId), nameof(SourceEntityId))]
public class RlsQueryCredential
{
    [Required] public string CompanyId { get; set; } = string.Empty;
    [Required][MaxLength(450)] public string SourceEntityId { get; set; } = string.Empty;

    /// <summary>Login this app created on the source. Generated, never operator-supplied.</summary>
    [Required][MaxLength(200)] public string Username { get; set; } = string.Empty;

    /// <summary>Encrypted password. Never serialized to a browser.</summary>
    [Required][JsonIgnore] public string SecretEncrypted { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>One table's worth of what an acting user may see, ready to be provisioned or rewritten.</summary>
/// <param name="CatalogTable">The catalog's name, <c>{schema}.{name}</c> — for ClickHouse the schema slot is the database.</param>
/// <param name="GrantedColumns">Columns the user may read. Null means "all of them" (no column restriction).</param>
/// <param name="Filters">Row filters for this table. Each becomes one restrictive policy.</param>
public sealed record RlsTablePlan(
    string CatalogTable,
    IReadOnlyList<string>? GrantedColumns,
    IReadOnlyList<RlsFilterPlan> Filters);

/// <summary>One row filter: a column restricted to a set of values.</summary>
/// <param name="AppliesToAllTables">
/// True for a legacy filter that names no table. It still applies on the rewrite path (to every
/// referenced table having the column), but it cannot be expressed as a native row policy — a policy has
/// to name one table — so a provisioner must report it rather than pretend to enforce it.
/// </param>
public sealed record RlsFilterPlan(
    string ColumnName,
    IReadOnlyList<string> AllowedValues,
    bool AppliesToAllTables = false);

/// <summary>Everything needed to provision one acting user's access to one dataset.</summary>
public sealed class RlsProvisioningPlan
{
    public required string CompanyId { get; init; }
    public required string SourceEntityId { get; init; }
    public required string DatasetId { get; init; }
    public required string UserId { get; init; }

    /// <summary>Role name on the source. Derived deterministically — see <c>RlsNaming</c>.</summary>
    public required string RoleName { get; init; }

    public required IReadOnlyList<RlsTablePlan> Tables { get; init; }

    /// <summary>
    /// True when nothing about this user is restricted. Such a user needs no role at all — but on the
    /// Native path they still need one, because the query account has no access of its own.
    /// </summary>
    public bool HasRestrictions =>
        Tables.Any(t => t.GrantedColumns is not null || t.Filters.Count > 0);
}

/// <summary>The outcome of checking that a source really is enforcing what our records say.</summary>
public sealed class RlsVerification
{
    public bool Ok { get; init; }

    /// <summary>Why not, phrased for the caller of the query endpoint. Null when <see cref="Ok"/>.</summary>
    public string? Problem { get; init; }

    public static RlsVerification Pass() => new() { Ok = true };
    public static RlsVerification Fail(string problem) => new() { Ok = false, Problem = problem };
}

/// <summary>
/// Everything that was set up for one acting user on one dataset: what this app's records say, what the
/// source actually holds, and whether the two agree.
/// </summary>
/// <remarks>
/// Deliberately reports intent and reality separately rather than a single merged view. The interesting
/// question when something looks wrong is precisely <i>which of the two</i> is off — a filter that was
/// never pushed looks identical to a policy someone deleted at the source, and only the comparison
/// distinguishes them.
/// </remarks>
public class RlsInspectionDto
{
    public string DatasetId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>How this source enforces the grants. Local datasets report <c>null</c> — see <see cref="AppliesToLivePath"/>.</summary>
    public RlsEnforcementMode? Mode { get; set; }

    /// <summary>
    /// False for a Local dataset, whose grants are enforced by rewriting against its own DuckDB snapshot
    /// and so have nothing provisioned anywhere to inspect.
    /// </summary>
    public bool AppliesToLivePath { get; set; }

    public DataSourceType? Engine { get; set; }

    /// <summary>What this app's grant tables say this user may see. Always populated for an external dataset.</summary>
    public List<RlsInspectionTableDto> Tables { get; set; } = new();

    // ---- Source-side objects. Only populated in Native mode; empty otherwise, because rewriting
    // creates nothing at the source.

    /// <summary>Role name at the source, when one applies.</summary>
    public string? RoleName { get; set; }

    /// <summary>The unprivileged account queries run as, when one has been provisioned.</summary>
    public string? QueryAccountName { get; set; }

    public bool? RoleExistsAtSource { get; set; }

    /// <summary>Row policies found at the source for this role.</summary>
    public List<RlsInspectionPolicyDto> SourcePolicies { get; set; } = new();

    /// <summary>
    /// Filters we hold that have <b>no</b> matching policy at the source. Non-empty means under-enforcement:
    /// a ClickHouse table with no policy is fully visible, so these are the dangerous ones.
    /// </summary>
    public List<string> MissingPolicies { get; set; } = new();

    /// <summary>Direct grants held by the query account. Must be empty — any entry voids every restriction.</summary>
    public List<string> QueryAccountDirectGrants { get; set; } = new();

    /// <summary>Whether the pre-query verification currently passes, and why not when it does not.</summary>
    public bool VerificationOk { get; set; }
    public string? VerificationProblem { get; set; }

    /// <summary>Set when the source could not be read at all, as opposed to disagreeing with our records.</summary>
    public string? SourceReadError { get; set; }
}

/// <summary>One table's grants as this app records them.</summary>
public class RlsInspectionTableDto
{
    public string TableName { get; set; } = string.Empty;

    /// <summary>Null when every column is readable — distinct from an empty list, which means none are.</summary>
    public List<string>? GrantedColumns { get; set; }

    public List<RlsInspectionFilterDto> Filters { get; set; } = new();
}

public class RlsInspectionFilterDto
{
    public string ColumnName { get; set; } = string.Empty;
    public List<string> AllowedValues { get; set; } = new();

    /// <summary>
    /// True for a legacy filter that names no table. It applies to every table having the column, and
    /// cannot be expressed as a native row policy — so on a Native source it is simply not enforced.
    /// </summary>
    public bool LegacyAllTables { get; set; }

    /// <summary>The policy name this filter maps to at the source, when one applies.</summary>
    public string? PolicyName { get; set; }

    /// <summary>Whether that policy was found at the source.</summary>
    public bool? PresentAtSource { get; set; }
}

/// <summary>A row policy as the source reports it.</summary>
public class RlsInspectionPolicyDto
{
    public string Name { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;

    /// <summary>The predicate the engine is applying, verbatim.</summary>
    public string? Condition { get; set; }

    /// <summary>Restrictive policies AND together; permissive ones OR and would widen access.</summary>
    public bool? IsRestrictive { get; set; }
}

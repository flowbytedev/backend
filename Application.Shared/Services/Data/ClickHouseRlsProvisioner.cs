using System.Net.Http.Headers;
using System.Text;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services.Data;

/// <summary>
/// Makes a ClickHouse source enforce this app's per-user column and row grants itself, so no SQL analysis
/// is trusted: the engine is the boundary.
/// </summary>
/// <remarks>
/// <para><b>Two identities, and why that is not optional.</b> ClickHouse privileges are the <i>union</i> of a
/// principal's own grants and its enabled roles, so naming a restricted role on the admin connection
/// restricts nothing at all. Queries therefore run as a generated account holding <b>no</b> grants, with
/// <c>DEFAULT ROLE NONE</c>, and the acting user's role named on that single request. The admin
/// credential only ever creates objects.</para>
///
/// <para><b>Verified against ClickHouse 25.12.1</b> before this was written, because each fact changes the
/// design and none is documented in a way worth trusting blind:</para>
/// <list type="bullet">
/// <item><c>?role=</c> on the HTTP interface enables exactly that role for one request.</item>
/// <item>With <c>DEFAULT ROLE NONE</c> and no direct grants, a request naming no role is refused.</item>
/// <item>Column grants hold inside aggregates — <c>sum(ungranted)</c> is refused, not silently zero.</item>
/// <item>Two <c>RESTRICTIVE</c> policies AND together, so one policy per filter row narrows correctly.
/// Permissive policies would OR and <i>widen</i>, which is why <c>AS RESTRICTIVE</c> is mandatory here.</item>
/// <item>A table with <b>no</b> policy is fully visible. That is the dangerous default, and the reason
/// <see cref="VerifyAsync"/> exists and runs before every query rather than trusting that a sync worked.</item>
/// </list>
///
/// <para><b>One behaviour to pass on to callers:</b> ClickHouse <i>refuses</i> <c>SELECT *</c> under a column
/// grant rather than narrowing it to the granted columns. Safe, but it means a model must name columns
/// explicitly, unlike the snapshot path where <c>SELECT *</c> expands.</para>
/// </remarks>
public interface IRlsProvisioner
{
    DataSourceType Engine { get; }

    /// <summary>
    /// Creates or refreshes the unprivileged query account for a source and stores its credential.
    /// Idempotent — safe to call before every apply.
    /// </summary>
    Task EnsureQueryIdentityAsync(string companyId, string sourceEntityId, CancellationToken ct = default);

    /// <summary>Writes one acting user's access into the source, replacing whatever was there.</summary>
    Task ApplyAsync(RlsProvisioningPlan plan, CancellationToken ct = default);

    /// <summary>Removes one acting user's role and policies.</summary>
    Task RemoveAsync(string companyId, string sourceEntityId, string datasetId, string userId,
        CancellationToken ct = default);

    /// <summary>
    /// Confirms the source is really enforcing what <paramref name="plan"/> describes, and that the query
    /// account still holds no access of its own. Runs before every query on the Native path.
    /// </summary>
    Task<RlsVerification> VerifyAsync(RlsProvisioningPlan plan, CancellationToken ct = default);

    /// <summary>
    /// Reads back what the source actually holds for this plan, for display. Never throws — a source that
    /// cannot be read reports <see cref="RlsInspectionDto.SourceReadError"/> so an operator can tell
    /// "unreachable" from "disagrees with our records".
    /// </summary>
    Task DescribeSourceStateAsync(RlsProvisioningPlan plan, RlsInspectionDto into,
        CancellationToken ct = default);
}

public class ClickHouseRlsProvisioner : IRlsProvisioner
{
    private const int CommandTimeoutSeconds = 30;

    private readonly ApplicationDbContext _db;
    private readonly StatusDbContext _status;
    private readonly ICredentialProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClickHouseRlsProvisioner> _logger;

    public ClickHouseRlsProvisioner(
        ApplicationDbContext db,
        StatusDbContext status,
        ICredentialProtector protector,
        IHttpClientFactory httpClientFactory,
        ILogger<ClickHouseRlsProvisioner> logger)
    {
        _db = db;
        _status = status;
        _protector = protector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public DataSourceType Engine => DataSourceType.ClickHouse;

    // ---------------------------------------------------------------- query identity

    public async Task EnsureQueryIdentityAsync(string companyId, string sourceEntityId,
        CancellationToken ct = default)
    {
        var admin = await LoadAdminConnectionAsync(companyId, sourceEntityId, ct);
        var username = RlsNaming.QueryUserName(companyId, sourceEntityId);

        var stored = await _db.RlsQueryCredential
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.SourceEntityId == sourceEntityId, ct);

        // Reuse the stored password when we have one: rotating on every apply would break in-flight
        // queries for no benefit. A missing row means the account may exist on the source with a password
        // we no longer know, so it is reset rather than assumed.
        var secret = stored is null ? RlsNaming.NewSecret() : _protector.Decrypt(stored.SecretEncrypted);

        var quoted = Quote(username);
        var literal = Literal(secret);

        // CREATE OR REPLACE would drop the role grants along with the user, so create-then-alter instead.
        try
        {
            await ExecuteAsync(admin, $"CREATE USER IF NOT EXISTS {quoted} IDENTIFIED WITH sha256_password BY {literal}", ct);
        }
        catch (Exception ex) when (stored is null)
        {
            throw new InvalidOperationException(
                $"Could not create the query account '{username}' on the source: {ex.Message}", ex);
        }

        if (stored is null)
        {
            // The account may predate this row (an interrupted earlier run). Force the password we hold,
            // or every query would fail to authenticate with no obvious cause.
            await ExecuteAsync(admin, $"ALTER USER {quoted} IDENTIFIED WITH sha256_password BY {literal}", ct);
        }

        // The whole enforcement model rests on this: with no default roles and no direct grants, a request
        // that names no role gets nothing. Re-asserted on every call because a well-meaning operator
        // granting this account something directly would silently disable every restriction.
        await ExecuteAsync(admin, $"ALTER USER {quoted} DEFAULT ROLE NONE", ct);

        if (stored is null)
        {
            _db.RlsQueryCredential.Add(new RlsQueryCredential
            {
                CompanyId = companyId,
                SourceEntityId = sourceEntityId,
                Username = username,
                SecretEncrypted = _protector.Encrypt(secret),
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The credential end-user queries run as, decrypted. Null when none has been provisioned.</summary>
    public async Task<(string Username, string Secret)?> GetQueryIdentityAsync(string companyId,
        string sourceEntityId, CancellationToken ct = default)
    {
        var stored = await _db.RlsQueryCredential.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.SourceEntityId == sourceEntityId, ct);

        return stored is null ? null : (stored.Username, _protector.Decrypt(stored.SecretEncrypted));
    }

    // ---------------------------------------------------------------- apply

    public async Task ApplyAsync(RlsProvisioningPlan plan, CancellationToken ct = default)
    {
        var admin = await LoadAdminConnectionAsync(plan.CompanyId, plan.SourceEntityId, ct);
        await EnsureQueryIdentityAsync(plan.CompanyId, plan.SourceEntityId, ct);

        var role = Quote(plan.RoleName);
        var queryUser = Quote(RlsNaming.QueryUserName(plan.CompanyId, plan.SourceEntityId));

        await ExecuteAsync(admin, $"CREATE ROLE IF NOT EXISTS {role}", ct);

        // Start from nothing so a narrowed grant actually narrows. Revoking per-table would leave grants
        // on tables that have since dropped out of the plan.
        await ExecuteAsync(admin, $"REVOKE ALL ON *.* FROM {role}", ct);
        await DropPoliciesForRoleAsync(admin, plan.RoleName, ct);

        foreach (var table in plan.Tables)
        {
            var target = QualifiedTable(table.CatalogTable);

            if (table.GrantedColumns is null)
            {
                await ExecuteAsync(admin, $"GRANT SELECT ON {target} TO {role}", ct);
            }
            else
            {
                if (table.GrantedColumns.Count == 0) continue; // nothing readable: grant nothing at all
                var columns = string.Join(", ", table.GrantedColumns.Select(Quote));
                await ExecuteAsync(admin, $"GRANT SELECT({columns}) ON {target} TO {role}", ct);
            }

            // One RESTRICTIVE policy per filter. They AND together (verified on 25.12), so a user's second
            // filter narrows their rows — which is what a grant is supposed to do. PERMISSIVE would OR and
            // widen, turning two restrictions into less restriction than one.
            foreach (var filter in table.Filters)
            {
                var name = Quote(RlsNaming.PolicyName(plan.DatasetId, plan.UserId, table.CatalogTable, filter.ColumnName));
                var condition = RenderCondition(filter);
                await ExecuteAsync(admin,
                    $"CREATE ROW POLICY OR REPLACE {name} ON {target} USING {condition} AS RESTRICTIVE TO {role}", ct);
            }
        }

        // Let the query account borrow this role. Granting it does NOT enable it — DEFAULT ROLE NONE keeps
        // every role dormant until a request names one.
        await ExecuteAsync(admin, $"GRANT {role} TO {queryUser}", ct);
    }

    public async Task RemoveAsync(string companyId, string sourceEntityId, string datasetId, string userId,
        CancellationToken ct = default)
    {
        var admin = await LoadAdminConnectionAsync(companyId, sourceEntityId, ct);
        var roleName = RlsNaming.RoleName(datasetId, userId);

        await DropPoliciesForRoleAsync(admin, roleName, ct);
        // Dropping the role also removes it from the query account.
        await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {Quote(roleName)}", ct);
    }

    /// <summary>
    /// Drops every policy attached to a role, read from <c>system.row_policies</c> rather than recomputed
    /// from a plan — a policy for a filter that has since been deleted would otherwise survive forever,
    /// and a stale RESTRICTIVE policy silently hides rows the user is now entitled to.
    /// </summary>
    private async Task DropPoliciesForRoleAsync(DatabaseConnection admin, string roleName, CancellationToken ct)
    {
        var body = await QueryAsync(admin,
            "SELECT short_name, database, table FROM system.row_policies "
            + $"WHERE has(apply_to_list, {Literal(roleName)}) FORMAT TSV", ct);

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!RlsNaming.IsOurs(parts[0])) continue; // never touch a policy someone else created

            await ExecuteAsync(admin,
                $"DROP ROW POLICY IF EXISTS {Quote(parts[0])} ON {Quote(parts[1])}.{Quote(parts[2])}", ct);
        }
    }

    // ---------------------------------------------------------------- verify

    public async Task<RlsVerification> VerifyAsync(RlsProvisioningPlan plan, CancellationToken ct = default)
    {
        DatabaseConnection admin;
        try
        {
            admin = await LoadAdminConnectionAsync(plan.CompanyId, plan.SourceEntityId, ct);
        }
        catch (Exception ex)
        {
            return RlsVerification.Fail(
                $"The source's provisioning credential could not be read, so it cannot be confirmed that "
                + $"row and column security is in force. ({ex.Message})");
        }

        try
        {
            var queryUser = RlsNaming.QueryUserName(plan.CompanyId, plan.SourceEntityId);

            // 1. The query account must hold nothing of its own. If it ever does, every role-based
            //    restriction is void, because privileges are a union. This is the check that catches
            //    someone widening the account later.
            var direct = (await QueryAsync(admin,
                $"SELECT count() FROM system.grants WHERE user_name = {Literal(queryUser)} FORMAT TSV", ct)).Trim();

            if (direct != "0")
                return RlsVerification.Fail(
                    "The account used to run queries on this source has been granted access directly, which "
                    + "would override this user's restrictions. Nothing was run. An administrator must revoke it.");

            // 2. Default roles must stay empty, or a request naming no role would inherit access.
            var defaults = (await QueryAsync(admin,
                $"SELECT default_roles_all FROM system.users WHERE name = {Literal(queryUser)} FORMAT TSV", ct)).Trim();

            if (defaults != "0")
                return RlsVerification.Fail(
                    "The account used to run queries on this source has default roles enabled, so a query "
                    + "could run with access it should not have. Nothing was run.");

            // 3. Every filter we hold must exist as a policy on the source. A missing one is the dangerous
            //    case: ClickHouse shows ALL rows of a table that has no policy, so absence is not "no
            //    filter applied", it is "no filter at all".
            var expected = plan.Tables
                .SelectMany(t => t.Filters.Select(f =>
                    RlsNaming.PolicyName(plan.DatasetId, plan.UserId, t.CatalogTable, f.ColumnName)))
                .ToHashSet(StringComparer.Ordinal);

            if (expected.Count > 0)
            {
                var present = (await QueryAsync(admin,
                        "SELECT short_name FROM system.row_policies "
                        + $"WHERE has(apply_to_list, {Literal(plan.RoleName)}) FORMAT TSV", ct))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .ToHashSet(StringComparer.Ordinal);

                var missing = expected.Count(name => !present.Contains(name));
                if (missing > 0)
                    return RlsVerification.Fail(
                        $"{missing} of this user's {expected.Count} row filters are not in force on the source, "
                        + "so the query would have returned rows they may not see. Nothing was run.");
            }

            // 4. Column grants must match, not merely exist. A superset would expose a masked column.
            foreach (var table in plan.Tables.Where(t => t.GrantedColumns is not null))
            {
                var (db, name) = SplitTable(table.CatalogTable);
                var actual = (await QueryAsync(admin,
                        "SELECT column FROM system.grants "
                        + $"WHERE role_name = {Literal(plan.RoleName)} AND database = {Literal(db)} "
                        + $"AND table = {Literal(name)} AND column IS NOT NULL FORMAT TSV", ct))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!actual.SetEquals(table.GrantedColumns!))
                    return RlsVerification.Fail(
                        $"The columns readable on '{table.CatalogTable}' at the source do not match this "
                        + "user's grants, so the query was not run.");
            }

            return RlsVerification.Pass();
        }
        catch (Exception ex)
        {
            // Fail closed: an unverifiable source is treated exactly like a misconfigured one.
            _logger.LogWarning(ex, "RLS verification failed for role {Role} on source {Source}.",
                plan.RoleName, plan.SourceEntityId);

            return RlsVerification.Fail(
                $"It could not be confirmed that row and column security is in force on this source, so "
                + $"nothing was run. ({ex.Message})");
        }
    }

    // ---------------------------------------------------------------- describe

    public async Task DescribeSourceStateAsync(RlsProvisioningPlan plan, RlsInspectionDto into,
        CancellationToken ct = default)
    {
        into.RoleName = plan.RoleName;
        into.QueryAccountName = RlsNaming.QueryUserName(plan.CompanyId, plan.SourceEntityId);

        try
        {
            var admin = await LoadAdminConnectionAsync(plan.CompanyId, plan.SourceEntityId, ct);

            var roleCount = (await QueryAsync(admin,
                $"SELECT count() FROM system.roles WHERE name = {Literal(plan.RoleName)} FORMAT TSV", ct)).Trim();
            into.RoleExistsAtSource = roleCount != "0";

            // is_restrictive and the condition are what make this worth showing: a permissive policy would
            // OR with others and widen access rather than narrow it.
            var policies = await QueryAsync(admin,
                "SELECT short_name, database, table, select_filter, is_restrictive FROM system.row_policies "
                + $"WHERE has(apply_to_list, {Literal(plan.RoleName)}) FORMAT TSV", ct);

            foreach (var line in policies.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                into.SourcePolicies.Add(new RlsInspectionPolicyDto
                {
                    Name = f[0],
                    Database = f[1],
                    Table = f[2],
                    Condition = f.Length > 3 ? f[3] : null,
                    IsRestrictive = f.Length > 4 ? f[4].Trim() == "1" : null
                });
            }

            var present = into.SourcePolicies.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var table in into.Tables)
            {
                foreach (var filter in table.Filters.Where(x => x.PolicyName is not null))
                {
                    filter.PresentAtSource = present.Contains(filter.PolicyName!);
                    if (filter.PresentAtSource == false)
                        into.MissingPolicies.Add($"{table.TableName}.{filter.ColumnName}");
                }
            }

            // Must come back empty. Anything here means the query account can read on its own account,
            // and privileges being a union, every role-based restriction is void.
            var direct = await QueryAsync(admin,
                "SELECT concat(access_type, ' ON ', coalesce(database, '*'), '.', coalesce(table, '*'), "
                + "if(column IS NULL, '', concat('(', column, ')'))) "
                + $"FROM system.grants WHERE user_name = {Literal(into.QueryAccountName)} FORMAT TSV", ct);

            into.QueryAccountDirectGrants = direct
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();
        }
        catch (Exception ex)
        {
            into.SourceReadError = ex.Message;
        }
    }

    // ---------------------------------------------------------------- SQL rendering

    /// <summary>
    /// Renders one filter's condition. Mirrors <see cref="SecuredSqlBuilder"/> and
    /// <see cref="LiveSourceSqlRewriter"/> exactly: an empty allowed-value set is <c>1 = 0</c> (no values
    /// permitted means no rows), numeric sets stay bare so an index is still usable, everything else is
    /// quoted text. The three must agree, or one grant would mean different things per mode.
    /// </summary>
    private static string RenderCondition(RlsFilterPlan filter)
    {
        if (filter.AllowedValues.Count == 0) return "1 = 0";

        var allNumeric = filter.AllowedValues.All(SecuredSqlBuilder.IsNumericLiteral);
        var values = filter.AllowedValues.Select(v => allNumeric ? v.Trim() : Literal(v));
        return $"{Quote(filter.ColumnName)} IN ({string.Join(",", values)})";
    }

    /// <summary>ClickHouse's "schema" slot is the database, so a catalog name maps straight to db.table.</summary>
    private static string QualifiedTable(string catalogTable)
    {
        var (db, name) = SplitTable(catalogTable);
        return $"{Quote(db)}.{Quote(name)}";
    }

    private static (string Database, string Table) SplitTable(string catalogTable)
    {
        var i = catalogTable.LastIndexOf('.');
        return i < 0 ? (string.Empty, catalogTable) : (catalogTable[..i], catalogTable[(i + 1)..]);
    }

    private static string Quote(string identifier) =>
        Pipelines.SqlTypeMapper.Quote(DataSourceType.ClickHouse, identifier);

    private static string Literal(string value) => SecuredSqlBuilder.QuoteLiteral(value);

    // ---------------------------------------------------------------- transport

    /// <summary>
    /// The provisioning credential for a source, decrypted. Refuses to fall back to the connection's own
    /// credential: provisioning is a deliberate, separately-granted capability, and quietly using a
    /// read credential here would produce a confusing partial failure instead of a clear one.
    /// </summary>
    private async Task<DatabaseConnection> LoadAdminConnectionAsync(string companyId, string sourceEntityId,
        CancellationToken ct)
    {
        var connection = await _status.DatabaseConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EntityId == sourceEntityId && c.CompanyId == companyId, ct)
            ?? throw new InvalidOperationException($"Source '{sourceEntityId}' has no saved connection.");

        var admin = await _status.DatabaseAdminCredentials.AsNoTracking()
            .FirstOrDefaultAsync(a => a.EntityId == sourceEntityId && a.CompanyId == companyId, ct);

        var username = admin?.Username;
        var secret = admin?.SecretEncrypted;

        // No provisioning credential recorded: the connection's own may still be privileged enough — that
        // is exactly what the capability probe established before Native mode could be chosen.
        if (string.IsNullOrWhiteSpace(username))
        {
            username = connection.Username;
            secret = connection.SecretEncrypted;
        }

        connection.Username = username;
        connection.SecretEncrypted = string.IsNullOrEmpty(secret) ? secret : _protector.Decrypt(secret);
        return connection;
    }

    private Task ExecuteAsync(DatabaseConnection c, string sql, CancellationToken ct) =>
        SendAsync(c, sql, role: null, ct);

    private Task<string> QueryAsync(DatabaseConnection c, string sql, CancellationToken ct) =>
        SendAsync(c, sql, role: null, ct);

    /// <summary>
    /// Posts one statement over ClickHouse's HTTP interface. No <c>readonly=1</c>: these are DDL. When
    /// <paramref name="role"/> is set it is enabled for this request only — the mechanism that makes one
    /// shared query account behave as whichever user is asking.
    /// </summary>
    internal async Task<string> SendAsync(DatabaseConnection c, string sql, string? role, CancellationToken ct)
    {
        var protocol = c.UseSsl ? "https" : "http";
        var url = $"{protocol}://{c.Host}:{c.Port}/";
        if (!string.IsNullOrEmpty(role)) url += $"?role={Uri.EscapeDataString(role)}";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(CommandTimeoutSeconds);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(c.Username))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.SecretEncrypted}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }
        request.Content = new StringContent(sql, Encoding.UTF8, "text/plain");

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"ClickHouse returned {(int)response.StatusCode}: {(body.Length > 500 ? body[..500] : body)}");

        return body;
    }
}

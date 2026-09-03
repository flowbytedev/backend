using System.Data.Common;
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
/// Answers one question about an external source: can we make the <b>source</b> enforce this app's
/// per-user column and row grants, or must Relay enforce them by rewriting the SQL it sends?
/// </summary>
/// <remarks>
/// Two separate things are checked, and conflating them produces a misleading prompt:
/// <list type="number">
/// <item>
/// <b>Does the engine have per-user row security at all?</b> SQL Server, PostgreSQL and ClickHouse do.
/// DuckDB and MySQL do not — so for those there is nothing to provision and no point asking anyone for
/// a stronger credential. Rewriting is the only option and the operator should be told that, not asked
/// a question with one answer.
/// </item>
/// <item>
/// <b>Can a credential we hold create those objects?</b> Checked against the elevated
/// <see cref="DatabaseAdminCredential"/> first and the connection's own credential second, so an
/// operator who has already supplied a provisioning user is not asked again.
/// </item>
/// </list>
/// <para>
/// Where a read-only privilege check is reliable it is preferred. Where it is not, the probe creates a
/// uniquely-named throwaway object and drops it again: that is the only way to answer "can create"
/// truthfully, and a permission check that is *nearly* right would be worse than none — an operator who
/// believes the source is enforcing the rules and is wrong stops checking.
/// </para>
/// </remarks>
public interface IRlsCapabilityProbe
{
    /// <summary>
    /// Probes <paramref name="sourceEntityId"/>. Never throws for a negative result — a source we cannot
    /// provision is an ordinary outcome, reported in the returned <see cref="RlsCapabilityReport"/>.
    /// Returns null only when the entity has no saved connection at all.
    /// </summary>
    Task<RlsCapabilityReport?> ProbeAsync(string sourceEntityId, string companyId, CancellationToken ct = default);
}

public class RlsCapabilityProbe : IRlsCapabilityProbe
{
    /// <summary>
    /// Engines with per-user row security this app can drive. DuckDB and MySQL are absent because they
    /// have none: MySQL offers only definer-rights views (a different design, and one that needs DDL on
    /// the source anyway), and DuckDB has no user concept at all — it is a local file.
    /// </summary>
    private static readonly HashSet<DataSourceType> NativeCapableEngines = new()
    {
        DataSourceType.SQLServer,
        DataSourceType.PostgreSQL,
        DataSourceType.ClickHouse
    };

    /// <summary>
    /// Engines whose provisioner is actually written. ClickHouse's behaviour was verified against 25.12
    /// before it was implemented; SQL Server and PostgreSQL have the feature but no provisioner here yet,
    /// so offering Native for them would only produce refusals.
    /// </summary>
    private static readonly HashSet<DataSourceType> ImplementedEngines = new()
    {
        DataSourceType.ClickHouse
    };

    private const int ProbeTimeoutSeconds = 15;

    private readonly StatusDbContext _status;
    private readonly ICredentialProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RlsCapabilityProbe> _logger;

    public RlsCapabilityProbe(
        StatusDbContext status,
        ICredentialProtector protector,
        IHttpClientFactory httpClientFactory,
        ILogger<RlsCapabilityProbe> logger)
    {
        _status = status;
        _protector = protector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<RlsCapabilityReport?> ProbeAsync(string sourceEntityId, string companyId,
        CancellationToken ct = default)
    {
        var connection = await _status.DatabaseConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EntityId == sourceEntityId && c.CompanyId == companyId, ct);
        if (connection == null) return null;

        var report = new RlsCapabilityReport
        {
            Engine = connection.DatabaseType,
            EngineSupportsNativeRls = NativeCapableEngines.Contains(connection.DatabaseType),
            NativeProvisioningImplemented = ImplementedEngines.Contains(connection.DatabaseType)
        };

        if (!report.EngineSupportsNativeRls)
        {
            report.Detail = connection.DatabaseType switch
            {
                DataSourceType.MySQL =>
                    "MySQL has no per-user row-level security, so the source cannot enforce these grants. "
                    + "Relay will rewrite each query instead.",
                DataSourceType.DuckDB =>
                    "DuckDB is a local file with no user accounts, so there is nothing to provision. "
                    + "Relay will rewrite each query instead.",
                _ =>
                    $"{connection.DatabaseType} has no per-user row-level security this app can drive. "
                    + "Relay will rewrite each query instead."
            };
            return report;
        }

        // Elevated credential first: if an operator has already supplied a provisioning user, testing the
        // weaker connection credential first would report a needless failure and re-prompt them.
        foreach (var candidate in await CandidateCredentialsAsync(connection, sourceEntityId, companyId, ct))
        {
            var (ok, error) = await TryProvisionAsync(candidate.Connection, ct);
            if (ok)
            {
                report.CurrentCredentialCanProvision = true;
                report.TestedUsername = candidate.Connection.Username;
                report.Detail = $"'{candidate.Connection.Username}' ({candidate.Label}) can create the "
                                + "objects needed to enforce these grants in the source.";
                return report;
            }

            // Keep the last failure as the reason; earlier candidates are usually the weaker ones.
            report.TestedUsername = candidate.Connection.Username;
            report.Detail = Truncate(
                $"'{candidate.Connection.Username}' ({candidate.Label}) cannot provision row security: {error}",
                2000);
        }

        return report;
    }

    /// <summary>
    /// The credentials worth trying, strongest first, each as a connection clone carrying that identity.
    /// </summary>
    private async Task<List<(string Label, DatabaseConnection Connection)>> CandidateCredentialsAsync(
        DatabaseConnection connection, string sourceEntityId, string companyId, CancellationToken ct)
    {
        var candidates = new List<(string, DatabaseConnection)>();

        var admin = await _status.DatabaseAdminCredentials.AsNoTracking()
            .FirstOrDefaultAsync(a => a.EntityId == sourceEntityId && a.CompanyId == companyId, ct);

        if (admin != null && !string.IsNullOrWhiteSpace(admin.Username))
            candidates.Add(("provisioning credential",
                WithCredential(connection, admin.Username, Decrypt(admin.SecretEncrypted))));

        if (!string.IsNullOrWhiteSpace(connection.Username))
            candidates.Add(("connection credential",
                WithCredential(connection, connection.Username, Decrypt(connection.SecretEncrypted))));

        return candidates;
    }

    /// <summary>
    /// A shallow copy of the connection carrying a different identity. Copied rather than mutated because
    /// the source object is shared with the caller, and a decrypted secret left on it would then travel
    /// further than intended.
    /// </summary>
    private static DatabaseConnection WithCredential(DatabaseConnection c, string? username, string? secret) => new()
    {
        Id = c.Id,
        EntityId = c.EntityId,
        CompanyId = c.CompanyId,
        DatabaseType = c.DatabaseType,
        Host = c.Host,
        Port = c.Port,
        DatabaseName = c.DatabaseName,
        UseSsl = c.UseSsl,
        FilePath = c.FilePath,
        Username = username,
        // Downstream helpers read the plaintext out of SecretEncrypted, matching
        // DatabaseAdminService.LoadDecryptedConnectionAsync.
        SecretEncrypted = secret
    };

    private string? Decrypt(string? value) =>
        string.IsNullOrEmpty(value) ? value : _protector.Decrypt(value);

    private Task<(bool Ok, string? Error)> TryProvisionAsync(DatabaseConnection c, CancellationToken ct) =>
        c.DatabaseType switch
        {
            DataSourceType.ClickHouse => TryClickHouseAsync(c, ct),
            DataSourceType.SQLServer => TrySqlServerAsync(c, ct),
            DataSourceType.PostgreSQL => TryPostgresAsync(c, ct),
            _ => Task.FromResult<(bool, string?)>((false, "Engine has no supported provisioning path."))
        };

    // ---------------------------------------------------------------- ClickHouse

    /// <summary>
    /// ClickHouse exposes no dependable read-only "may I create a role" check across versions, so this
    /// creates a throwaway role and drops it. Both statements are attempted; the drop runs even when the
    /// create failed, so a partial success never leaves an orphan behind.
    /// </summary>
    private async Task<(bool, string?)> TryClickHouseAsync(DatabaseConnection c, CancellationToken ct)
    {
        var role = ProbeObjectName();
        try
        {
            await ClickHouseAsync(c, $"CREATE ROLE {role}", ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { await ClickHouseAsync(c, $"DROP ROLE IF EXISTS {role}", ct); }
            catch (Exception ex)
            {
                // Worth a log: a role we created and could not drop is litter in the operator's cluster.
                _logger.LogWarning(ex, "Could not drop RLS probe role {Role} on {Host}.", role, c.Host);
            }
        }
    }

    private async Task ClickHouseAsync(DatabaseConnection c, string sql, CancellationToken ct)
    {
        var protocol = c.UseSsl ? "https" : "http";
        // No readonly=1: this statement is DDL by design, which that setting would reject.
        var url = $"{protocol}://{c.Host}:{c.Port}/";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds);

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
            throw new InvalidOperationException($"ClickHouse returned {(int)response.StatusCode}: {Truncate(body, 400)}");
    }

    // ---------------------------------------------------------------- SQL Server

    /// <summary>
    /// SQL Server needs a schema-bound predicate function plus a security policy. Both are covered by
    /// database-level permissions that <c>HAS_PERMS_BY_NAME</c> reports honestly, so no DDL is needed to
    /// find out.
    /// </summary>
    private async Task<(bool, string?)> TrySqlServerAsync(DatabaseConnection c, CancellationToken ct)
    {
        try
        {
            await using var conn = ExternalConnectionFactory.Create(c, readOnly: false);
            await conn.OpenAsync(ct);

            var canFunction = await ScalarBoolAsync(conn,
                "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE FUNCTION')", ct);
            var canPolicy = await ScalarBoolAsync(conn,
                "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY SECURITY POLICY')", ct);

            if (canFunction && canPolicy) return (true, null);

            var missing = new List<string>();
            if (!canFunction) missing.Add("CREATE FUNCTION");
            if (!canPolicy) missing.Add("ALTER ANY SECURITY POLICY");
            return (false, $"missing database permission(s): {string.Join(", ", missing)}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- PostgreSQL

    /// <summary>
    /// PostgreSQL policies are created by the <b>table owner</b> (or a superuser), and
    /// <c>ALTER TABLE ... ENABLE ROW LEVEL SECURITY</c> is likewise an owner-only statement — so having
    /// <c>CREATEROLE</c> is not sufficient. Both are checked from the catalog, read-only.
    /// </summary>
    private async Task<(bool, string?)> TryPostgresAsync(DatabaseConnection c, CancellationToken ct)
    {
        try
        {
            await using var conn = ExternalConnectionFactory.Create(c, readOnly: false);
            await conn.OpenAsync(ct);

            var superuser = await ScalarBoolAsync(conn,
                "SELECT COALESCE(rolsuper, false) FROM pg_roles WHERE rolname = current_user", ct);
            if (superuser) return (true, null);

            var canCreateRole = await ScalarBoolAsync(conn,
                "SELECT COALESCE(rolcreaterole, false) FROM pg_roles WHERE rolname = current_user", ct);

            // Ownership of at least one ordinary table: without it, no policy can be created anywhere and
            // the credential is useless for this purpose however many other rights it holds.
            var ownsTables = await ScalarBoolAsync(conn,
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relkind = 'r'
                      AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                      AND pg_get_userbyid(c.relowner) = current_user)
                """, ct);

            if (canCreateRole && ownsTables) return (true, null);

            var missing = new List<string>();
            if (!canCreateRole) missing.Add("CREATEROLE");
            if (!ownsTables) missing.Add("ownership of the tables to be secured");
            return (false, $"missing: {string.Join(", ", missing)}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<bool> ScalarBoolAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = ProbeTimeoutSeconds;
        var value = await command.ExecuteScalarAsync(ct);
        return value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            _ => false
        };
    }

    /// <summary>
    /// A throwaway object name that cannot need quoting or escaping: a fixed prefix plus hex from a GUID.
    /// Built rather than parameterised because no engine accepts parameters in DDL.
    /// </summary>
    private static string ProbeObjectName() =>
        "relay_rls_probe_" + Guid.NewGuid().ToString("N");

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}

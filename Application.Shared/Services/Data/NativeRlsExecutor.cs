using System.Text;
using System.Text.Json;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

/// <summary>
/// Runs an end user's SQL against a source that enforces their grants itself: as the unprivileged query
/// account, with only that user's role enabled for the one request.
/// </summary>
/// <remarks>
/// Separate from <c>IDatabaseTableService.ExecuteQueryAsync</c> because that resolves the connection's own
/// credential, which is the opposite of what is needed here — the whole point is to run as a principal
/// with no access of its own. Verifying first and executing here keeps both halves in one place, so it is
/// not possible to add a caller that executes without verifying.
/// </remarks>
public interface INativeRlsExecutor
{
    /// <summary>
    /// Verifies the source is enforcing <paramref name="plan"/>, then runs <paramref name="sql"/> under
    /// that user's role. Returns the failed verification instead of a result when it does not hold —
    /// nothing is executed in that case.
    /// </summary>
    Task<(SqlQueryResult? Result, RlsVerification Verification)> ExecuteAsync(
        RlsProvisioningPlan plan, string sql, int maxRows, CancellationToken ct = default);
}

public class NativeRlsExecutor : INativeRlsExecutor
{
    private readonly StatusDbContext _status;
    private readonly ClickHouseRlsProvisioner _clickHouse;
    private readonly ICredentialProtector _protector;

    public NativeRlsExecutor(
        StatusDbContext status,
        ClickHouseRlsProvisioner clickHouse,
        ICredentialProtector protector)
    {
        _status = status;
        _clickHouse = clickHouse;
        _protector = protector;
    }

    public async Task<(SqlQueryResult?, RlsVerification)> ExecuteAsync(
        RlsProvisioningPlan plan, string sql, int maxRows, CancellationToken ct = default)
    {
        var connection = await _status.DatabaseConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EntityId == plan.SourceEntityId && c.CompanyId == plan.CompanyId, ct);

        if (connection == null)
            return (null, RlsVerification.Fail(
                "The dataset's source connection could not be read, so access cannot be verified."));

        if (connection.DatabaseType != DataSourceType.ClickHouse)
            return (null, RlsVerification.Fail(
                $"Source-enforced row and column security is not implemented for {connection.DatabaseType} yet. "
                + "Switch this source to query rewriting."));

        // Verification runs every time, not once at provisioning. ClickHouse shows every row of a table
        // that has no policy, so a sync that half-applied — or a policy someone deleted — reads as "no
        // restrictions" rather than as an error. Checking here is what turns that into a refusal.
        var verification = await _clickHouse.VerifyAsync(plan, ct);
        if (!verification.Ok) return (null, verification);

        var identity = await _clickHouse.GetQueryIdentityAsync(plan.CompanyId, plan.SourceEntityId, ct);
        if (identity is null)
            return (null, RlsVerification.Fail(
                "This source has no query account provisioned, so the query was not run. "
                + "Re-save the source's row and column security settings to create one."));

        // Run as the unprivileged account with the acting user's role named for this request only.
        var asQueryUser = CloneWith(connection, identity.Value.Username, identity.Value.Secret);

        var result = new SqlQueryResult();
        try
        {
            var body = await _clickHouse.SendAsync(asQueryUser,
                $"{sql}\nFORMAT JSONCompact", plan.RoleName, ct);

            Parse(body, maxRows, result);
        }
        catch (Exception ex)
        {
            // Includes the engine's own ACCESS_DENIED for a masked column, which is the enforcement
            // working. Passed through so the model can correct itself — notably by naming columns instead
            // of SELECT *, which ClickHouse refuses rather than narrowing under a column grant.
            result.Error = ex.Message;
        }

        return (result, RlsVerification.Pass());
    }

    /// <summary>
    /// A copy of the connection carrying the query identity. Copied rather than mutated because the
    /// tracked entity is shared, and a decrypted secret left on it would travel further than intended.
    /// </summary>
    private static DatabaseConnection CloneWith(DatabaseConnection c, string username, string secret) => new()
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
        // Downstream reads the plaintext out of SecretEncrypted, matching DatabaseAdminService.
        SecretEncrypted = secret
    };

    /// <summary>
    /// Reads ClickHouse's JSONCompact body into the shared result shape. Row values are kept as strings
    /// where they arrive as strings — ClickHouse returns 64-bit integers as JSON strings, the same quirk
    /// <c>DatabaseTableService</c> documents.
    /// </summary>
    private static void Parse(string body, int maxRows, SqlQueryResult result)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Array)
        {
            foreach (var column in meta.EnumerateArray())
            {
                result.Columns.Add(new Column
                {
                    Name = column.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    DataType = column.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty
                });
            }
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;

        foreach (var row in data.EnumerateArray())
        {
            if (result.Rows.Count >= maxRows) { result.Truncated = true; break; }

            var values = new Dictionary<string, object?>();
            var i = 0;
            foreach (var cell in row.EnumerateArray())
            {
                var name = i < result.Columns.Count ? result.Columns[i].Name : $"c{i}";
                values[name ?? $"c{i}"] = cell.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => cell.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => cell.GetRawText(),
                    _ => cell.GetRawText()
                };
                i++;
            }
            result.Rows.Add(values);
        }

        result.RowsReturned = result.Rows.Count;
        result.IsSelect = true;
    }
}

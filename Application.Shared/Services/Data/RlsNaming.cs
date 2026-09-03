using System.Security.Cryptography;
using System.Text;

namespace Application.Shared.Services.Data;

/// <summary>
/// Names for the objects Relay creates inside a customer's database.
/// </summary>
/// <remarks>
/// Every name is a fixed prefix plus lowercase hex, so it is safe as a bare identifier on any engine and
/// needs no quoting or escaping — which matters because no engine accepts parameters in DDL, so these
/// names are always concatenated into a statement. Dataset ids and user ids are arbitrary strings
/// (GUIDs, Entra object ids) and some engines cap identifier length, so they are hashed rather than
/// embedded.
/// <para>
/// Derivation is deterministic and stateless: the name for a (dataset, user) pair can always be
/// recomputed, so nothing has to be stored to find a role again, and reconciliation can list objects by
/// prefix and compare against the set it expects.
/// </para>
/// </remarks>
public static class RlsNaming
{
    /// <summary>Shared prefix, so a reconciler can find everything this app owns and nothing else.</summary>
    public const string Prefix = "relay_rls_";

    /// <summary>Prefix for the unprivileged per-source query account.</summary>
    public const string QueryUserPrefix = "relay_query_";

    /// <summary>
    /// Role holding one acting user's access to one dataset. Both ids go into the hash, so the same
    /// person on two datasets gets two roles and the grants cannot bleed between them.
    /// </summary>
    public static string RoleName(string datasetId, string userId) =>
        Prefix + Hash($"{datasetId}{userId}");

    /// <summary>
    /// Row policy for one (table, column) filter. The table and column are part of the hash so two
    /// filters on one table do not collide — they must coexist, since restrictive policies AND together
    /// and that is how a user's second filter narrows their access.
    /// </summary>
    public static string PolicyName(string datasetId, string userId, string table, string column) =>
        Prefix + "p_" + Hash($"{datasetId}{userId}{table}{column}");

    /// <summary>The unprivileged account that runs queries against one source.</summary>
    public static string QueryUserName(string companyId, string sourceEntityId) =>
        QueryUserPrefix + Hash($"{companyId}{sourceEntityId}");

    /// <summary>True for a name this app created — used by reconciliation to leave others alone.</summary>
    public static bool IsOurs(string? name) =>
        name is not null
        && (name.StartsWith(Prefix, StringComparison.Ordinal)
            || name.StartsWith(QueryUserPrefix, StringComparison.Ordinal));

    /// <summary>
    /// A generated password for an account this app owns. 32 bytes of CSPRNG output as hex — no symbol
    /// classes, so it cannot collide with any engine's literal escaping rules on the way in.
    /// </summary>
    public static string NewSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// First 24 hex characters of SHA-256. Truncated because some engines cap identifier length, and
    /// 96 bits is far past any collision risk for the number of roles one deployment will ever hold.
    /// </summary>
    private static string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }
}

using System.Data.Common;
using Application.Shared.Enums;
using Application.Shared.Models;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace Application.Shared.Services;

/// <summary>
/// Builds ADO.NET connections to a customer's external database from a <see cref="DatabaseConnection"/>,
/// and holds the per-dialect identifier quoting rules.
/// <para>
/// Two callers share this: <see cref="DatabaseTableService"/> (always <c>readOnly: true</c> — discovery,
/// sampling, freshness, ingestion reads) and <see cref="DatabaseAdminService"/> (<c>readOnly: false</c> —
/// it has to issue DDL). The read-only flag is what switches <c>ApplicationIntent=ReadOnly</c> and DuckDB's
/// <c>ACCESS_MODE=READ_ONLY</c> on and off; Postgres and MySQL get their read-only behaviour from
/// <c>SET SESSION … READ ONLY</c> instead, which the admin service simply doesn't issue.
/// </para>
/// The quoting helpers live here rather than in either service because they are the injection defence —
/// there must be exactly one copy of them.
/// <para>
/// Note the field naming trap inherited from <see cref="DatabaseConnection"/>: callers decrypt the secret
/// in place, so by the time these builders run <c>c.SecretEncrypted</c> holds <b>plaintext</b>.
/// </para>
/// </summary>
internal static class ExternalConnectionFactory
{
    /// <summary>Builds an unopened ADO.NET connection for an engine. ClickHouse is HTTP-only (no ADO).</summary>
    public static DbConnection Create(DatabaseConnection c, bool readOnly) =>
        CreateForCatalog(c, null, readOnly);

    /// <summary>
    /// As <see cref="Create"/>, but connecting to <paramref name="catalog"/> instead of the connection's own
    /// database. Needed for SQL Server, where <c>CREATE LOGIN</c> is a server-level statement that has to run
    /// against <c>master</c> while <c>CREATE USER</c> runs against the target database.
    /// </summary>
    public static DbConnection CreateForCatalog(
        DatabaseConnection c, string? catalog, bool readOnly, bool forBulkLoad = false) => c.DatabaseType switch
    {
        DataSourceType.SQLServer => new SqlConnection(BuildSqlServerConnectionString(c, catalog, readOnly)),
        DataSourceType.PostgreSQL => new NpgsqlConnection(BuildPostgresConnectionString(c, catalog)),
        DataSourceType.MySQL => new MySqlConnection(BuildMySqlConnectionString(c, catalog, forBulkLoad)),
        DataSourceType.DuckDB => new DuckDBConnection(BuildDuckDbConnectionString(c, readOnly)),
        _ => throw new NotSupportedException($"No ADO.NET driver for database type: {c.DatabaseType}.")
    };

    private static string BuildSqlServerConnectionString(DatabaseConnection c, string? catalog, bool readOnly)
    {
        var server = c.Port > 0 ? $"{c.Host},{c.Port}" : c.Host;
        // ApplicationIntent=ReadOnly signals read-only intent (and routes to a readable secondary on AlwaysOn).
        // Harmless/ignored on standalone servers. It must NOT be set for admin work, which writes.
        var intent = readOnly ? "ApplicationIntent=ReadOnly;" : string.Empty;
        return $"Server={server};Initial Catalog={catalog ?? c.DatabaseName};User ID={c.Username};Password={c.SecretEncrypted};" +
               $"Encrypt={(c.UseSsl ? "True" : "False")};TrustServerCertificate=True;{intent}Connection Timeout=15;";
    }

    private static string BuildPostgresConnectionString(DatabaseConnection c, string? catalog) =>
        $"Host={c.Host};Port={(c.Port > 0 ? c.Port : 5432)};Database={catalog ?? c.DatabaseName};Username={c.Username};Password={c.SecretEncrypted};" +
        $"SSL Mode={(c.UseSsl ? "Require" : "Prefer")};Trust Server Certificate=true;Timeout=15;";

    private static string BuildMySqlConnectionString(
        DatabaseConnection c, string? catalog, bool forBulkLoad = false)
    {
        // MySqlBulkCopy refuses to run without this, and the server also needs local_infile=1. Set only for
        // a bulk load, because it widens what the client is allowed to do with local files.
        var localInfile = forBulkLoad ? "AllowLoadLocalInfile=True;" : string.Empty;

        return $"Server={c.Host};Port={(c.Port > 0 ? c.Port : 3306)};Database={catalog ?? c.DatabaseName};" +
               $"User ID={c.Username};Password={c.SecretEncrypted};" +
               $"SslMode={(c.UseSsl ? "Required" : "Preferred")};{localInfile}Connection Timeout=15;";
    }

    private static string BuildDuckDbConnectionString(DatabaseConnection c, bool readOnly) =>
        // READ_ONLY opens the file without taking a write lock and rejects any modification.
        readOnly ? $"DataSource={c.FilePath};ACCESS_MODE=READ_ONLY" : $"DataSource={c.FilePath}";

    /// <summary>Command to put the session into read-only mode before querying, where the engine supports it.</summary>
    public static string? ReadOnlySetupFor(DataSourceType type) => type switch
    {
        DataSourceType.PostgreSQL => "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY",
        DataSourceType.MySQL => "SET SESSION TRANSACTION READ ONLY",
        _ => null
    };

    public static int DefaultPort(DataSourceType type) => type switch
    {
        DataSourceType.SQLServer => 1433,
        DataSourceType.PostgreSQL => 5432,
        DataSourceType.MySQL => 3306,
        DataSourceType.ClickHouse => 8123,
        _ => 0
    };

    /// <summary>Quotes a single identifier for the engine, escaping the quote char (neutralizes injection).</summary>
    public static string QuoteIdentifier(DataSourceType type, string id) => type switch
    {
        DataSourceType.SQLServer => $"[{id.Replace("]", "]]")}]",
        DataSourceType.PostgreSQL => $"\"{id.Replace("\"", "\"\"")}\"",
        DataSourceType.DuckDB => $"\"{id.Replace("\"", "\"\"")}\"",
        DataSourceType.MySQL => $"`{id.Replace("`", "``")}`",
        DataSourceType.ClickHouse => $"`{id.Replace("`", "``")}`",
        _ => id
    };

    /// <summary>Quotes a possibly-qualified "{schema}.{table}" name, splitting on the first dot.</summary>
    public static string QuoteQualified(DataSourceType type, string fullName)
    {
        var idx = fullName.IndexOf('.');
        if (idx <= 0 || idx == fullName.Length - 1)
            return QuoteIdentifier(type, fullName);
        return $"{QuoteIdentifier(type, fullName[..idx])}.{QuoteIdentifier(type, fullName[(idx + 1)..])}";
    }

    /// <summary>Escapes a value for use inside a single-quoted SQL string literal.</summary>
    public static string EscapeLiteral(string? value) => (value ?? string.Empty).Replace("'", "''");
}

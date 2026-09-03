using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Models;

// Per-user dataset access, row-level security, and personal pins/default. All company-scoped and keyed
// per user (composite [PrimaryKey], matching the DatasetUserTable pattern).
//
// Where these grants ARE enforced, which differs by path — do not assume:
//   * api/dataset/{id}/query/run (PublicSqlQueryService) ENFORCES column masking and RLS, by rewriting
//     every referenced table into a secured relation. It is the only row-returning path that does.
//   * The catalog endpoints (PublicDatasetApiService) apply column grants when TRIMMING metadata, and
//     serve RLS filters verbatim for a consumer to read.
//   * QueryController, the table-data endpoints and api/external/* apply TABLE grants only. Within an
//     allowed table they return every column and every row.
// Column/RLS grants are also served to the chat app via the public API. A consumer cannot enforce RLS
// even if it wants to: UserRlsFilter carries no table name, so only this side — which can read the
// schema — can resolve which tables a filter applies to.

/// <summary>
/// Grants a user access to a specific column of a dataset table. A (user, dataset, table) with NO rows
/// here means the user has FULL column access to that table; one or more rows restrict them to exactly
/// those columns. Mirrors the DatasetUserTable "restrict-by-presence" semantics at the column level.
/// </summary>
[PrimaryKey(nameof(CompanyId), nameof(UserId), nameof(DatasetId), nameof(TableName), nameof(ColumnName))]
public class DatasetUserColumn
{
    [Required] public string CompanyId { get; set; } = string.Empty;
    [Required] public string UserId { get; set; } = string.Empty;
    [Required] public string DatasetId { get; set; } = string.Empty;
    [Required] public string TableName { get; set; } = string.Empty;
    [Required] public string ColumnName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Row-level security: restricts the rows a user may see, within <see cref="TableName"/>, to those where
/// <see cref="ColumnName"/> is one of <see cref="AllowedValues"/> (a JSON array string, e.g.
/// ["FOOD","BEVERAGE"]).
/// </summary>
[PrimaryKey(nameof(CompanyId), nameof(UserId), nameof(DatasetId), nameof(TableName), nameof(ColumnName))]
public class UserRlsFilter
{
    [Required] public string CompanyId { get; set; } = string.Empty;
    [Required] public string UserId { get; set; } = string.Empty;
    [Required] public string DatasetId { get; set; } = string.Empty;

    /// <summary>
    /// The table this filter applies to. <see cref="AllTablesSentinel"/> (empty) means the legacy
    /// behaviour: every referenced table that happens to have a column of this name. Use
    /// <see cref="AppliesToAllTables"/> rather than comparing to empty at call sites.
    /// </summary>
    [Required] public string TableName { get; set; } = string.Empty;

    [Required] public string ColumnName { get; set; } = string.Empty;

    /// <summary>JSON array of allowed values, e.g. ["FOOD","BEVERAGE"].</summary>
    [Required] public string AllowedValues { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// The <see cref="TableName"/> value meaning "every table having this column". Only ever present on
    /// rows created before filters were table-scoped; the editor always writes a real table name.
    /// </summary>
    public const string AllTablesSentinel = "";

    /// <summary>
    /// True for a legacy, table-less filter. Such a filter cannot be expressed as a native row policy
    /// (those name one table), so a provisioner must refuse it rather than approximate it.
    /// </summary>
    public bool AppliesToAllTables => string.IsNullOrWhiteSpace(TableName);

    /// <summary>True when this filter governs <paramref name="table"/>.</summary>
    public bool AppliesTo(string table) =>
        AppliesToAllTables || string.Equals(TableName, table, StringComparison.OrdinalIgnoreCase);

    /// <summary>The allowed values deserialized as a list (empty on malformed/empty JSON).</summary>
    public List<string> GetAllowedValuesList()
    {
        try { return JsonSerializer.Deserialize<List<string>>(AllowedValues) ?? new(); }
        catch { return new(); }
    }
}

/// <summary>A user's personal pin of a dataset (presence = pinned). Pinned datasets sort to the top of the list.</summary>
[PrimaryKey(nameof(CompanyId), nameof(UserId), nameof(DatasetId))]
public class UserDatasetPin
{
    [Required] public string CompanyId { get; set; } = string.Empty;
    [Required] public string UserId { get; set; } = string.Empty;
    [Required] public string DatasetId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user's personal pin of a table within a dataset (presence = pinned).</summary>
[PrimaryKey(nameof(CompanyId), nameof(UserId), nameof(DatasetId), nameof(TableName))]
public class UserTablePin
{
    [Required] public string CompanyId { get; set; } = string.Empty;
    [Required] public string UserId { get; set; } = string.Empty;
    [Required] public string DatasetId { get; set; } = string.Empty;
    [Required] public string TableName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The single dataset a user has chosen as their default (one row per user + company).</summary>
[PrimaryKey(nameof(CompanyId), nameof(UserId))]
public class UserDefaultDataset
{
    [Required] public string CompanyId { get; set; } = string.Empty;
    [Required] public string UserId { get; set; } = string.Empty;
    [Required] public string DatasetId { get; set; } = string.Empty;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}

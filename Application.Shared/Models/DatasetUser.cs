using Application.Shared.Models.User;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Application.Shared.Models;

[PrimaryKey(nameof(DatasetId), nameof(UserId))]
public class DatasetUser
{
    [Required]
    public string DatasetId { get; set; } = string.Empty;
    public Dataset? Dataset { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    // public ApplicationUser? User { get; set; }

    [Required]
    public DatasetUserType Type { get; set; } = DatasetUserType.Viewer;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
}

public enum DatasetUserType
{
    Admin = 0,
    Editor = 1,
    Viewer = 2
}

/// <summary>
/// Scopes a dataset share to specific tables. A (DatasetId, UserId) with NO rows here has access to
/// ALL tables in the dataset; one or more rows restrict the user to exactly those tables.
/// </summary>
[PrimaryKey(nameof(DatasetId), nameof(UserId), nameof(TableName))]
public class DatasetUserTable
{
    [Required]
    public string DatasetId { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string TableName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ShareDatasetRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string DatasetId { get; set; } = string.Empty;

    [Required]
    public DatasetUserType UserType { get; set; } = DatasetUserType.Viewer;

    /// <summary>
    /// Tables this user may access. Null or empty = all tables (full dataset access). When set, the
    /// user's table scope is REPLACED with exactly these tables.
    /// </summary>
    public List<string>? Tables { get; set; }

    /// <summary>
    /// Optional per-table column scoping. Key = table name; value = the columns the user may access in
    /// that table. A table absent here (or with an empty list) = ALL columns of that table. The whole
    /// column scope is REPLACED on each share. Independent of <see cref="Tables"/>.
    /// </summary>
    public Dictionary<string, List<string>>? Columns { get; set; }
}

/// <summary>Additive single-table grant (used by the "share this table" action). Does not downgrade
/// a user who already has full dataset access.</summary>
public class GrantTableShareRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string DatasetId { get; set; } = string.Empty;

    [Required]
    public string TableName { get; set; } = string.Empty;

    [Required]
    public DatasetUserType UserType { get; set; } = DatasetUserType.Viewer;

    /// <summary>Columns of <see cref="TableName"/> the user may access. Null = leave column scope
    /// untouched; empty list = all columns (clears any restriction); non-empty = restrict to these.
    /// Only applied when the user is scoped to specific tables (not full-access/Admin).</summary>
    public List<string>? Columns { get; set; }
}

public class DatasetUserDto
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public DatasetUserType Type { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Tables this user is scoped to. Empty = all tables (full access).</summary>
    public List<string> Tables { get; set; } = new();

    /// <summary>Per-table column restrictions (table → allowed columns). A table absent here = all columns.</summary>
    public Dictionary<string, List<string>> Columns { get; set; } = new();
}

/// <summary>One row-level-security filter: restrict rows to those where <see cref="ColumnName"/> is one of
/// <see cref="AllowedValues"/>. Column names are matched dataset-wide (across the dataset's tables).</summary>
public class RlsFilterItem
{
    [Required]
    public string ColumnName { get; set; } = string.Empty;
    public List<string> AllowedValues { get; set; } = new();
}

/// <summary>Full per-user access to one dataset, applied atomically by the data-admin User Access page:
/// dataset-level type + table scope + per-table column scope + row-level-security filters.</summary>
public class SetUserAccessRequest
{
    public DatasetUserType Type { get; set; } = DatasetUserType.Viewer;

    /// <summary>Tables the user may access; null or empty = all tables.</summary>
    public List<string>? Tables { get; set; }

    /// <summary>Per-table column restrictions (table → allowed columns); a table absent = all columns.</summary>
    public Dictionary<string, List<string>>? Columns { get; set; }

    /// <summary>Row-level-security filters (column → allowed values).</summary>
    public List<RlsFilterItem> Rls { get; set; } = new();

    /// <summary>When true, ALL of the user's access to this dataset (share + tables + columns + RLS) is removed.</summary>
    public bool Remove { get; set; }
}

/// <summary>The current per-user access to a dataset, for prefilling the User Access editor.</summary>
public class UserDatasetAccessDto
{
    public bool HasAccess { get; set; }
    public DatasetUserType Type { get; set; } = DatasetUserType.Viewer;

    /// <summary>Scoped tables; empty = all tables.</summary>
    public List<string> Tables { get; set; } = new();

    /// <summary>Per-table column restrictions (table → columns); a table absent = all columns.</summary>
    public Dictionary<string, List<string>> Columns { get; set; } = new();

    public List<RlsFilterItem> Rls { get; set; } = new();
}

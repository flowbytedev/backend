using System.Text.Json.Serialization;

namespace Application.Shared.Models.Data;

// DTOs for the API-key-secured public API consumed by the external chat app. The shapes here match the
// chat client's deserialization EXACTLY: outer properties are camelCase (ASP.NET default), while the
// data-catalog inner fields are snake_case, pinned via [JsonPropertyName]. Enums are emitted as numbers.

/// <summary>Dataset as the chat app's DatasetSwitcher expects it. Connection fields (host/port/username/
/// password/driver) are intentionally left empty here — they're sourced from credentials only when the
/// consumer actually connects. <c>type</c> is the chat's DatasetType number (0=MSSQL,1=DUCKDB,2=CLICKHOUSE,
/// 3=POSTGRESQL,4=MYSQL,…).</summary>
/// <summary>
/// Body of <c>POST api/dataset/user</c> — the chat app auto-provisioning the acting user's personal
/// dataset. The tenant and owner are NOT in the body: company comes from the API key and the user from the
/// <c>X-User-Id</c> header, so a caller can only ever create a dataset for itself.
/// </summary>
public class CreateUserDatasetRequest
{
    /// <summary>The user's username. Not name-validated like an app-created dataset — see the service.</summary>
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>Directory for the dataset's DuckDB file, e.g. <c>E://duckdb/chat</c>.</summary>
    public string Path { get; set; } = string.Empty;
}

public class PublicDatasetDto
{
    public string Id { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsMessageDataset { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}

public class DataCatalogDto
{
    // Outer camelCase → "tableMetadata".
    public List<TableMetadataDto> TableMetadata { get; set; } = new();
}

public class TableMetadataDto
{
    [JsonPropertyName("dataset_id")] public string DatasetId { get; set; } = string.Empty;
    [JsonPropertyName("table_name")] public string TableName { get; set; } = string.Empty;
    [JsonPropertyName("table_description")] public string TableDescription { get; set; } = string.Empty;
    [JsonPropertyName("companyId")] public string CompanyId { get; set; } = string.Empty;
    [JsonPropertyName("columns")] public List<ColumnMetadataDto> Columns { get; set; } = new();
}

public class ColumnMetadataDto
{
    [JsonPropertyName("dataset_id")] public string DatasetId { get; set; } = string.Empty;
    [JsonPropertyName("table_name")] public string TableName { get; set; } = string.Empty;
    [JsonPropertyName("column_name")] public string ColumnName { get; set; } = string.Empty;
    [JsonPropertyName("column_description")] public string ColumnDescription { get; set; } = string.Empty;
    [JsonPropertyName("data_type")] public string DataType { get; set; } = string.Empty;
    [JsonPropertyName("max_length")] public int? MaxLength { get; set; }
    [JsonPropertyName("is_nullable")] public bool IsNullable { get; set; } = true;
    [JsonPropertyName("is_primary_key")] public bool IsPrimaryKey { get; set; }
    [JsonPropertyName("table_relations")] public List<TableRelationDto> TableRelations { get; set; } = new();

    // The rest of the persisted semantic layer (dataset_column_doc), added for AI consumers writing SQL.
    // GetSavedColumnDocsAsync already returned all of this and only Description was being read.
    // Additive JSON — existing consumers ignore what they don't bind.

    /// <summary>Business-friendly name, e.g. "Net Amount" for <c>net_amt_acy</c>.</summary>
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }

    /// <summary>
    /// Semantic role (currency, percentage, identifier, quantity, date, category…). What stops a model
    /// SUM-ing a percentage or averaging an id.
    /// </summary>
    [JsonPropertyName("semantic_type")] public string? SemanticType { get; set; }

    /// <summary>Unit of measure, disambiguating otherwise identical numeric columns.</summary>
    [JsonPropertyName("unit")] public string? Unit { get; set; }

    /// <summary>
    /// Whether the column is flagged as personal data. <b>Advisory only, never an access control</b> — see
    /// <see cref="DatasetColumnDoc"/>, where the flag is documented as surfaced for human review and never
    /// used to enforce access. A consumer may use it to avoid selecting the column; it must not present it
    /// to anyone as a guarantee.
    /// </summary>
    [JsonPropertyName("is_pii")] public bool IsPii { get; set; }

    /// <summary>The kind of personal data, when <see cref="IsPii"/> is set. Advisory, as above.</summary>
    [JsonPropertyName("pii_type")] public string? PiiType { get; set; }
}

public class TableRelationDto
{
    [JsonPropertyName("dataset_id")] public string DatasetId { get; set; } = string.Empty;
    [JsonPropertyName("table_name")] public string TableName { get; set; } = string.Empty;
    [JsonPropertyName("column_name")] public string ColumnName { get; set; } = string.Empty;
    [JsonPropertyName("reference_table_name")] public string ReferenceTableName { get; set; } = string.Empty;
    [JsonPropertyName("reference_column_name")] public string ReferenceColumnName { get; set; } = string.Empty;
}

/// <summary>A user's access to one table, with nested per-column grants. <c>hasFullAccess</c> is true when
/// no column restrictions exist for the (user, dataset, table) — i.e. all columns are visible.</summary>
public class UserTableAccessDto
{
    public string UserId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool HasFullAccess { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public List<UserColumnAccessDto> ColumnAccess { get; set; } = new();
}

public class UserColumnAccessDto
{
    public string UserId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
}

/// <summary>Row-level security filter as the chat expects it — <c>allowedValues</c> is a JSON string.</summary>
public class UserRlsFilterDto
{
    public string UserId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;

    /// <summary>
    /// The table this filter applies to. Empty is the legacy "every table having this column" sentinel —
    /// a consumer describing the filter to a user (or to a model) should say so rather than naming a table.
    /// </summary>
    public string TableName { get; set; } = string.Empty;

    public string ColumnName { get; set; } = string.Empty;
    public string AllowedValues { get; set; } = "[]";
    public string CompanyId { get; set; } = string.Empty;
}

/// <summary>Everything needed to connect to a dataset's source, server-to-server only. For an External
/// dataset these are the decrypted DB connection details; for a Local dataset it's the DuckDB file path.
/// <see cref="Password"/> is decrypted — never expose this to a browser.</summary>
public class DatasetCredentialDto
{
    public string Id { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Engine as the chat's DatasetType number (0=MSSQL,1=DUCKDB,2=CLICKHOUSE,3=POSTGRESQL,4=MYSQL).</summary>
    public int Type { get; set; }

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Optional driver hint; empty when the consumer derives it from <see cref="Type"/>.</summary>
    public string Driver { get; set; } = string.Empty;

    /// <summary>DuckDB file path for a Local dataset (or a DuckDB-file external source); empty otherwise.</summary>
    public string FilePath { get; set; } = string.Empty;

    public string? ApiKey { get; set; }
    public string? ConnectionString { get; set; }
}

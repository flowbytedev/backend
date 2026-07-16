using System.Text.Json.Serialization;

namespace Application.Shared.Models.Data;

// DTOs for the API-key-secured public API consumed by the external chat app. The shapes here match the
// chat client's deserialization EXACTLY: outer properties are camelCase (ASP.NET default), while the
// data-catalog inner fields are snake_case, pinned via [JsonPropertyName]. Enums are emitted as numbers.

/// <summary>Dataset as the chat app's DatasetSwitcher expects it. Connection fields (host/port/username/
/// password/driver) are intentionally left empty here — they're sourced from credentials only when the
/// consumer actually connects. <c>type</c> is the chat's DatasetType number (0=MSSQL,1=DUCKDB,2=CLICKHOUSE,
/// 3=POSTGRESQL,4=MYSQL,…).</summary>
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
    public string ColumnName { get; set; } = string.Empty;
    public string AllowedValues { get; set; } = "[]";
    public string CompanyId { get; set; } = string.Empty;
}

/// <summary>Decrypted connection credentials for a dataset's external source. Server-to-server only.</summary>
public class DatasetCredentialDto
{
    public string Id { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? ConnectionString { get; set; }
}

namespace Application.Shared.Enums;

public enum DataSourceType
{
    SQLServer,
    DuckDB,
    ClickHouse,
    PostgreSQL,
    MySQL,

    /// <summary>
    /// Read only, and only by a pipeline's database step. Not SQL, so it cannot back a dataset. Stored as an
    /// int, and PublicDatasetApiService forwards the int to the chat app, so new engines go on the end.
    /// </summary>
    MongoDB
}

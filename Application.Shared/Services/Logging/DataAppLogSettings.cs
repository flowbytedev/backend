namespace Application.Shared.Services.Logging;

/// <summary>
/// Binds the <c>DataAppLog</c> configuration section — the ClickHouse target for the
/// Datasets &amp; Tables audit/usage log.
/// </summary>
public class DataAppLogSettings
{
    /// <summary>When false, logging is a no-op (filter + endpoint do nothing, no connection is opened).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ADO connection string for ClickHouse.Client, e.g.
    /// <c>Host=...;Port=8123;Database=data_app_log;Username=...;Password=...</c>.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Fully-qualified destination table.</summary>
    public string Table { get; set; } = "data_app_log.data_app_log";

    /// <summary>Max rows flushed per ClickHouse insert.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Max time an entry waits in the buffer before a partial batch is flushed (ms).</summary>
    public int FlushIntervalMs { get; set; } = 2000;

    /// <summary>Bounded in-memory queue capacity; excess entries are dropped rather than blocking requests.</summary>
    public int QueueCapacity { get; set; } = 10000;
}

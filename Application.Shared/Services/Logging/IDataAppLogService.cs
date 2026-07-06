using Application.Shared.Models.Logging;

namespace Application.Shared.Services.Logging;

/// <summary>
/// Buffered writer for the Datasets &amp; Tables audit/usage log (ClickHouse <c>data_app_log</c>).
/// <see cref="Enqueue"/> is non-blocking and must never throw — logging must not affect the request.
/// </summary>
public interface IDataAppLogService
{
    /// <summary>True when logging is configured and enabled.</summary>
    bool Enabled { get; }

    /// <summary>Queues an entry for background insertion. Never blocks; drops silently if the buffer is full.</summary>
    void Enqueue(DataAppLogEntry entry);

    /// <summary>Creates the ClickHouse database/table if missing. Safe to call repeatedly; swallows failures.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drains the buffer and writes batches to ClickHouse until <paramref name="cancellationToken"/> is
    /// cancelled. Intended to be run by a single background worker.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Reads a filtered, paged slice of the log for the viewer. Always scoped to a single company.</summary>
    Task<DataAppLogQueryResult> QueryAsync(DataAppLogQuery query, CancellationToken cancellationToken = default);
}

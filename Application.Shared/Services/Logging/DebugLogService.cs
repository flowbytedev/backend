using System.Text.Json;
using Application.Shared.Models.Logging;
using Application.Shared.Services;

namespace Application.Shared.Services.Logging;

/// <summary>Debug-log severity levels used across feature instrumentation.</summary>
public static class DebugLevel
{
    public const string Debug = "Debug";
    public const string Info = "Info";
    public const string Warn = "Warn";
    public const string Error = "Error";
}

public interface IDebugLogService
{
    /// <summary>
    /// Emits a debug log entry for a company — but only when the ClickHouse log store is globally
    /// enabled AND the company has debug logging turned on (per <see cref="ICompanySettingsService"/>).
    /// Fire-and-forget: swallows all failures so instrumentation never breaks or slows the caller.
    /// </summary>
    Task LogAsync(
        string companyId,
        string level,
        string category,
        string message,
        string? datasetId = null,
        string? tableName = null,
        string? userId = null,
        string? userName = null,
        object? context = null,
        long durationMs = 0,
        string? error = null,
        CancellationToken ct = default);
}

/// <summary>
/// Thin gate + adapter that turns feature debug calls into <see cref="DataAppLogEntry"/> rows
/// (source = <c>debug</c>) on the shared ClickHouse writer, subject to the per-company toggle.
/// Scoped, because the per-company enable check reads through the scoped settings service.
/// </summary>
public class DebugLogService : IDebugLogService
{
    private readonly IDataAppLogService _log;
    private readonly ICompanySettingsService _settings;

    public DebugLogService(IDataAppLogService log, ICompanySettingsService settings)
    {
        _log = log;
        _settings = settings;
    }

    public async Task LogAsync(
        string companyId,
        string level,
        string category,
        string message,
        string? datasetId = null,
        string? tableName = null,
        string? userId = null,
        string? userName = null,
        object? context = null,
        long durationMs = 0,
        string? error = null,
        CancellationToken ct = default)
    {
        try
        {
            // Cheap global gate first (no DB), then the cached per-company toggle.
            if (string.IsNullOrWhiteSpace(companyId) || !_log.Enabled)
                return;
            if (!await _settings.IsDebugLoggingEnabledAsync(companyId, ct))
                return;

            _log.Enqueue(new DataAppLogEntry
            {
                EventTime = DateTime.UtcNow,
                CompanyId = companyId,
                UserId = userId ?? string.Empty,
                UserName = userName ?? string.Empty,
                Source = "debug",
                Area = "dataset",
                Action = string.IsNullOrWhiteSpace(category) ? "debug" : category.ToLowerInvariant(),
                DatasetId = datasetId ?? string.Empty,
                TableName = tableName ?? string.Empty,
                Level = level ?? string.Empty,
                Category = category ?? string.Empty,
                Message = message ?? string.Empty,
                DurationMs = durationMs,
                Success = !string.Equals(level, DebugLevel.Error, StringComparison.OrdinalIgnoreCase),
                Error = error ?? string.Empty,
                Details = context is null ? string.Empty : Serialize(context),
            });
        }
        catch
        {
            // Debug logging must never break or slow the caller.
        }
    }

    private static string Serialize(object context)
    {
        try { return JsonSerializer.Serialize(context); }
        catch { return string.Empty; }
    }
}

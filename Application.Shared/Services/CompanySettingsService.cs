using Application.Shared.Data;
using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Application.Shared.Services;

public interface ICompanySettingsService
{
    /// <summary>The company's settings row, or a transient default (debug off, default export format) when none is saved yet.</summary>
    Task<CompanySettings> GetAsync(string companyId, CancellationToken ct = default);

    /// <summary>
    /// Upserts the company's settings and refreshes the cached values. A null
    /// <see cref="CompanySettingsDto.ExportDateFormat"/> leaves the stored format alone, so a caller that
    /// only means to toggle debug logging can't blank it.
    /// </summary>
    Task SaveAsync(string companyId, CompanySettingsDto settings, string? userId, CancellationToken ct = default);

    /// <summary>Upserts the debug-logging toggle for a company and refreshes the cached value.</summary>
    Task SetDebugLoggingAsync(string companyId, bool enabled, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Cheap, cached read for the log write-path: true when this company has debug logging enabled.
    /// Cached for a short window so per-request logging doesn't hit the database on every entry.
    /// </summary>
    Task<bool> IsDebugLoggingEnabledAsync(string companyId, CancellationToken ct = default);

    /// <summary>
    /// Cached read for the CSV export path: the date pattern to write dates with, already resolved to a
    /// supported value (falls back to <see cref="ExportDateFormats.Default"/>).
    /// </summary>
    Task<string> GetExportDateFormatAsync(string companyId, CancellationToken ct = default);

    /// <summary>
    /// Cached read for the freshness sweep: whether this company is checked, and who to tell.
    /// <para>
    /// Cached like the others because the sweep asks once per company per pass and the answer changes
    /// rarely — but the TTL is short enough that turning the toggle off takes effect on the next sweep
    /// rather than the one after.
    /// </para>
    /// </summary>
    Task<CompanyFreshnessSettings> GetFreshnessAsync(string companyId, CancellationToken ct = default);

    /// <summary>
    /// Cached read for a pipeline run: the folder this company's source steps stage their files in,
    /// already resolved to a concrete path (falls back to the OS temp folder).
    /// <para>
    /// Resolved rather than raw, so no caller has to remember the fallback — a source step that forgot it
    /// would write to whatever <c>Path.Combine</c> made of a null, and that is a bug you find in
    /// production at 3am. The folder itself is created by the run, not here.
    /// </para>
    /// </summary>
    Task<string> GetPipelineWorkingDirectoryAsync(string companyId, CancellationToken ct = default);
}

/// <summary>
/// What the freshness sweep needs from a company's settings. A record rather than two calls so the sweep
/// cannot read a stale enabled flag against a fresh recipient list.
/// </summary>
/// <param name="Enabled">Null when the company has never chosen; the caller applies its own default.</param>
public sealed record CompanyFreshnessSettings(bool? Enabled, string? Recipients);

public class CompanySettingsService : ICompanySettingsService
{
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public CompanySettingsService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string DebugCacheKey(string companyId) => $"company-settings:debug:{companyId}";
    private static string ExportFormatCacheKey(string companyId) => $"company-settings:export-date-format:{companyId}";
    private static string FreshnessCacheKey(string companyId) => $"company-settings:freshness:{companyId}";
    private static string WorkingDirectoryCacheKey(string companyId) => $"company-settings:pipeline-workdir:{companyId}";

    public async Task<CompanySettings> GetAsync(string companyId, CancellationToken ct = default)
    {
        var row = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        return row ?? new CompanySettings
        {
            CompanyId = companyId,
            DebugLoggingEnabled = false,
            ExportDateFormat = ExportDateFormats.Default,
            // Left null rather than defaulted to true: "never chosen" is a state the freshness reader
            // resolves against the deployment default, and inventing a value here would hide that.
            FreshnessChecksEnabled = null,
            FreshnessAlertRecipients = null,
            // Same reasoning: null is "never chosen", which PipelineWorkspacePath resolves to the OS
            // temp folder. Storing the resolved path here would freeze one machine's temp folder into a
            // row the scheduler on another machine then reads.
            PipelineWorkingDirectory = null,
        };
    }

    public async Task SaveAsync(string companyId, CompanySettingsDto settings, string? userId, CancellationToken ct = default)
    {
        var row = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        var now = DateTime.UtcNow;

        // Null = "not being changed"; anything else is normalised so an unsupported pattern can never reach
        // the export path (see ExportDateFormats.Resolve).
        var format = settings.ExportDateFormat == null
            ? row?.ExportDateFormat
            : ExportDateFormats.Resolve(settings.ExportDateFormat);

        // Same "null = not being changed" rule. The recipient list additionally distinguishes an empty
        // string, which is how the UI says "nobody" — normalising that back to null is what makes
        // "cleared" and "never set" behave identically downstream.
        var freshnessEnabled = settings.FreshnessChecksEnabled ?? row?.FreshnessChecksEnabled;
        var recipients = settings.FreshnessAlertRecipients == null
            ? row?.FreshnessAlertRecipients
            : AlertRecipients.Normalize(settings.FreshnessAlertRecipients);

        // And again for the working folder: null leaves it alone, "" clears it back to the default.
        var workingDirectory = settings.PipelineWorkingDirectory == null
            ? row?.PipelineWorkingDirectory
            : PipelineWorkspacePath.Normalize(settings.PipelineWorkingDirectory);

        if (row == null)
        {
            row = new CompanySettings
            {
                CompanyId = companyId,
                DebugLoggingEnabled = settings.DebugLoggingEnabled,
                ExportDateFormat = format,
                FreshnessChecksEnabled = freshnessEnabled,
                FreshnessAlertRecipients = recipients,
                PipelineWorkingDirectory = workingDirectory,
                CreatedBy = userId,
                CreatedOn = now,
                ModifiedBy = userId,
                ModifiedOn = now,
            };
            _db.CompanySettings.Add(row);
        }
        else
        {
            row.DebugLoggingEnabled = settings.DebugLoggingEnabled;
            row.ExportDateFormat = format;
            row.FreshnessChecksEnabled = freshnessEnabled;
            row.FreshnessAlertRecipients = recipients;
            row.PipelineWorkingDirectory = workingDirectory;
            row.ModifiedBy = userId;
            row.ModifiedOn = now;
        }

        await _db.SaveChangesAsync(ct);

        // Refresh the cached reads so a just-saved change takes effect on the next request rather than
        // after the TTL lapses.
        _cache.Set(DebugCacheKey(companyId), row.DebugLoggingEnabled, CacheTtl);
        _cache.Set(ExportFormatCacheKey(companyId), ExportDateFormats.Resolve(row.ExportDateFormat), CacheTtl);
        _cache.Set(FreshnessCacheKey(companyId),
            new CompanyFreshnessSettings(row.FreshnessChecksEnabled, row.FreshnessAlertRecipients), CacheTtl);
        _cache.Set(WorkingDirectoryCacheKey(companyId),
            PipelineWorkspacePath.Resolve(row.PipelineWorkingDirectory), CacheTtl);
    }

    public Task SetDebugLoggingAsync(string companyId, bool enabled, string? userId, CancellationToken ct = default)
        => SaveAsync(companyId, new CompanySettingsDto { DebugLoggingEnabled = enabled }, userId, ct);

    public async Task<bool> IsDebugLoggingEnabledAsync(string companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return false;
        if (_cache.TryGetValue(DebugCacheKey(companyId), out bool cached))
            return cached;

        var enabled = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.DebugLoggingEnabled)
            .FirstOrDefaultAsync(ct);

        _cache.Set(DebugCacheKey(companyId), enabled, CacheTtl);
        return enabled;
    }

    public async Task<string> GetExportDateFormatAsync(string companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return ExportDateFormats.Default;
        if (_cache.TryGetValue(ExportFormatCacheKey(companyId), out string? cached) && cached != null)
            return cached;

        var stored = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.ExportDateFormat)
            .FirstOrDefaultAsync(ct);

        var format = ExportDateFormats.Resolve(stored);
        _cache.Set(ExportFormatCacheKey(companyId), format, CacheTtl);
        return format;
    }

    public async Task<CompanyFreshnessSettings> GetFreshnessAsync(
        string companyId, CancellationToken ct = default)
    {
        // An absent company is "never chosen" rather than "disabled": the caller's default decides, and
        // returning false here would silently switch the feature off for a caller that passed a blank id.
        if (string.IsNullOrWhiteSpace(companyId)) return new(null, null);

        if (_cache.TryGetValue(FreshnessCacheKey(companyId), out CompanyFreshnessSettings? cached)
            && cached is not null)
            return cached;

        var stored = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => new { s.FreshnessChecksEnabled, s.FreshnessAlertRecipients })
            .FirstOrDefaultAsync(ct);

        var settings = new CompanyFreshnessSettings(
            stored?.FreshnessChecksEnabled, stored?.FreshnessAlertRecipients);

        _cache.Set(FreshnessCacheKey(companyId), settings, CacheTtl);
        return settings;
    }

    public async Task<string> GetPipelineWorkingDirectoryAsync(
        string companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return PipelineWorkspacePath.Default;

        if (_cache.TryGetValue(WorkingDirectoryCacheKey(companyId), out string? cached) && cached != null)
            return cached;

        var stored = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.PipelineWorkingDirectory)
            .FirstOrDefaultAsync(ct);

        var directory = PipelineWorkspacePath.Resolve(stored);
        _cache.Set(WorkingDirectoryCacheKey(companyId), directory, CacheTtl);
        return directory;
    }
}

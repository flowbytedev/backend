using Application.Shared.Data;
using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Application.Shared.Services;

public interface ICompanySettingsService
{
    /// <summary>The company's settings row, or a transient default (all flags off) when none is saved yet.</summary>
    Task<CompanySettings> GetAsync(string companyId, CancellationToken ct = default);

    /// <summary>Upserts the debug-logging toggle for a company and refreshes the cached value.</summary>
    Task SetDebugLoggingAsync(string companyId, bool enabled, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Cheap, cached read for the log write-path: true when this company has debug logging enabled.
    /// Cached for a short window so per-request logging doesn't hit the database on every entry.
    /// </summary>
    Task<bool> IsDebugLoggingEnabledAsync(string companyId, CancellationToken ct = default);
}

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

    private static string CacheKey(string companyId) => $"company-settings:debug:{companyId}";

    public async Task<CompanySettings> GetAsync(string companyId, CancellationToken ct = default)
    {
        var row = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        return row ?? new CompanySettings { CompanyId = companyId, DebugLoggingEnabled = false };
    }

    public async Task SetDebugLoggingAsync(string companyId, bool enabled, string? userId, CancellationToken ct = default)
    {
        var row = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        var now = DateTime.UtcNow;
        if (row == null)
        {
            row = new CompanySettings
            {
                CompanyId = companyId,
                DebugLoggingEnabled = enabled,
                CreatedBy = userId,
                CreatedOn = now,
                ModifiedBy = userId,
                ModifiedOn = now,
            };
            _db.CompanySettings.Add(row);
        }
        else
        {
            row.DebugLoggingEnabled = enabled;
            row.ModifiedBy = userId;
            row.ModifiedOn = now;
        }

        await _db.SaveChangesAsync(ct);
        _cache.Set(CacheKey(companyId), enabled, CacheTtl);
    }

    public async Task<bool> IsDebugLoggingEnabledAsync(string companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return false;
        if (_cache.TryGetValue(CacheKey(companyId), out bool cached))
            return cached;

        var enabled = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.DebugLoggingEnabled)
            .FirstOrDefaultAsync(ct);

        _cache.Set(CacheKey(companyId), enabled, CacheTtl);
        return enabled;
    }
}

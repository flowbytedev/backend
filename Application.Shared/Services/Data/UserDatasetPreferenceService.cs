using Application.Shared.Data;
using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

/// <summary>Per-user dataset/table pins and the user's default dataset. Pins are presence-based rows
/// (row exists ⇒ pinned); the default is a single row per (company, user).</summary>
public interface IUserDatasetPreferenceService
{
    Task<(HashSet<string> PinnedDatasetIds, string? DefaultDatasetId)> GetDatasetPreferencesAsync(string companyId, string userId, CancellationToken ct = default);
    Task<HashSet<string>> GetPinnedTablesAsync(string companyId, string userId, string datasetId, CancellationToken ct = default);

    Task SetDatasetPinnedAsync(string companyId, string userId, string datasetId, bool pinned, CancellationToken ct = default);
    Task SetTablePinnedAsync(string companyId, string userId, string datasetId, string tableName, bool pinned, CancellationToken ct = default);
    Task SetDefaultDatasetAsync(string companyId, string userId, string datasetId, CancellationToken ct = default);
    Task ClearDefaultDatasetAsync(string companyId, string userId, CancellationToken ct = default);
}

public class UserDatasetPreferenceService : IUserDatasetPreferenceService
{
    private readonly ApplicationDbContext _db;

    public UserDatasetPreferenceService(ApplicationDbContext db) => _db = db;

    public async Task<(HashSet<string> PinnedDatasetIds, string? DefaultDatasetId)> GetDatasetPreferencesAsync(string companyId, string userId, CancellationToken ct = default)
    {
        var pinned = await _db.UserDatasetPin
            .Where(p => p.CompanyId == companyId && p.UserId == userId)
            .Select(p => p.DatasetId)
            .ToListAsync(ct);

        var defaultId = await _db.UserDefaultDataset
            .Where(d => d.CompanyId == companyId && d.UserId == userId)
            .Select(d => d.DatasetId)
            .FirstOrDefaultAsync(ct);

        return (pinned.ToHashSet(StringComparer.Ordinal), string.IsNullOrEmpty(defaultId) ? null : defaultId);
    }

    public async Task<HashSet<string>> GetPinnedTablesAsync(string companyId, string userId, string datasetId, CancellationToken ct = default)
    {
        var names = await _db.UserTablePin
            .Where(p => p.CompanyId == companyId && p.UserId == userId && p.DatasetId == datasetId)
            .Select(p => p.TableName)
            .ToListAsync(ct);
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetDatasetPinnedAsync(string companyId, string userId, string datasetId, bool pinned, CancellationToken ct = default)
    {
        var existing = await _db.UserDatasetPin
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.UserId == userId && p.DatasetId == datasetId, ct);

        if (pinned && existing == null)
            _db.UserDatasetPin.Add(new UserDatasetPin { CompanyId = companyId, UserId = userId, DatasetId = datasetId });
        else if (!pinned && existing != null)
            _db.UserDatasetPin.Remove(existing);
        else
            return;

        await _db.SaveChangesAsync(ct);
    }

    public async Task SetTablePinnedAsync(string companyId, string userId, string datasetId, string tableName, bool pinned, CancellationToken ct = default)
    {
        var existing = await _db.UserTablePin
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.UserId == userId && p.DatasetId == datasetId && p.TableName == tableName, ct);

        if (pinned && existing == null)
            _db.UserTablePin.Add(new UserTablePin { CompanyId = companyId, UserId = userId, DatasetId = datasetId, TableName = tableName });
        else if (!pinned && existing != null)
            _db.UserTablePin.Remove(existing);
        else
            return;

        await _db.SaveChangesAsync(ct);
    }

    public async Task SetDefaultDatasetAsync(string companyId, string userId, string datasetId, CancellationToken ct = default)
    {
        var existing = await _db.UserDefaultDataset
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.UserId == userId, ct);

        if (existing == null)
            _db.UserDefaultDataset.Add(new UserDefaultDataset { CompanyId = companyId, UserId = userId, DatasetId = datasetId, ModifiedAt = DateTime.UtcNow });
        else
        {
            existing.DatasetId = datasetId;
            existing.ModifiedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearDefaultDatasetAsync(string companyId, string userId, CancellationToken ct = default)
    {
        var existing = await _db.UserDefaultDataset
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.UserId == userId, ct);
        if (existing != null)
        {
            _db.UserDefaultDataset.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }
    }
}

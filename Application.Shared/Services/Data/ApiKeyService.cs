using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Application.Shared.Data;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

public interface IApiKeyService
{
    Task<List<ApiKeyDto>> GetKeysAsync(string companyId);
    Task<CreateApiKeyResult> CreateKeyAsync(string companyId, CreateApiKeyRequest request, string? createdBy);
    Task<ApiKeyDto?> UpdateKeyAsync(string companyId, string id, UpdateApiKeyRequest request);

    /// <summary>
    /// Issues a brand-new secret for an existing key, keeping its name, expiry and grants.
    /// Returns null when the key does not exist; throws when it is revoked.
    /// </summary>
    Task<CreateApiKeyResult?> RotateKeyAsync(string companyId, string id);
    Task<bool> RevokeKeyAsync(string companyId, string id);
    Task<bool> DeleteKeyAsync(string companyId, string id);

    /// <summary>
    /// Resolves a raw key presented by an external caller. Returns the active <see cref="ApiKey"/>
    /// (with its scopes) or null when the key is unknown, revoked, or expired. Touches LastUsedAt.
    /// </summary>
    Task<ApiKey?> ValidateAsync(string rawKey);

    /// <summary>True when the key holds a grant covering the given dataset/table and operation.</summary>
    bool IsInScope(ApiKey key, string datasetId, string? tableName, ApiKeyOperation operation);
}

public class ApiKeyService : IApiKeyService
{
    // Raw keys look like "fb_<43 base64url chars>"; we expose the first chunk as a recognizable prefix.
    private const string KeyPrefixTag = "fb_";
    private const int KeySecretBytes = 32;
    private const int DisplayPrefixLength = 11;

    private readonly ApplicationDbContext _db;

    public ApiKeyService(ApplicationDbContext db) => _db = db;

    public async Task<List<ApiKeyDto>> GetKeysAsync(string companyId)
    {
        var keys = await _db.ApiKey
            .Where(k => k.CompanyId == companyId)
            .Include(k => k.Scopes)
            .OrderByDescending(k => k.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        var datasetNames = await GetDatasetNamesAsync(companyId);
        return keys.Select(k => ToDto(k, datasetNames)).ToList();
    }

    public async Task<CreateApiKeyResult> CreateKeyAsync(string companyId, CreateApiKeyRequest request, string? createdBy)
    {
        var (rawKey, prefix, hash) = GenerateKey();

        var key = new ApiKey
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            Name = request.Name?.Trim() ?? string.Empty,
            KeyHash = hash,
            KeyPrefix = prefix,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
        };

        var validDatasetIds = await GetCompanyDatasetIdsAsync(companyId);
        key.Scopes = BuildScopes(key.Id, request.Scopes, validDatasetIds);

        _db.ApiKey.Add(key);
        await _db.SaveChangesAsync();

        var datasetNames = await GetDatasetNamesAsync(companyId);
        return new CreateApiKeyResult { Key = ToDto(key, datasetNames), PlainTextKey = rawKey };
    }

    public async Task<ApiKeyDto?> UpdateKeyAsync(string companyId, string id, UpdateApiKeyRequest request)
    {
        var key = await _db.ApiKey
            .Where(k => k.CompanyId == companyId && k.Id == id)
            .Include(k => k.Scopes)
            .FirstOrDefaultAsync();
        if (key == null) return null;

        key.Name = request.Name?.Trim() ?? key.Name;
        key.ExpiresAt = request.ExpiresAt;
        key.ModifiedAt = DateTime.UtcNow;

        // Replace the scope set wholesale — simplest correct semantics for an edit form.
        _db.ApiKeyScope.RemoveRange(key.Scopes);
        var validDatasetIds = await GetCompanyDatasetIdsAsync(companyId);
        key.Scopes = BuildScopes(key.Id, request.Scopes, validDatasetIds);

        await _db.SaveChangesAsync();

        var datasetNames = await GetDatasetNamesAsync(companyId);
        return ToDto(key, datasetNames);
    }

    public async Task<CreateApiKeyResult?> RotateKeyAsync(string companyId, string id)
    {
        var key = await _db.ApiKey
            .Where(k => k.CompanyId == companyId && k.Id == id)
            .Include(k => k.Scopes)
            .FirstOrDefaultAsync();
        if (key == null) return null;

        // A revoked key stays dead. Handing it a working secret would silently restore access that
        // someone deliberately cut off; creating a new key makes that decision visible instead.
        if (key.RevokedAt != null)
            throw new InvalidOperationException("This key is revoked. Create a new key instead of resetting this one.");

        var (rawKey, prefix, hash) = GenerateKey();
        key.KeyHash = hash;
        key.KeyPrefix = prefix;
        // The new secret has never been used; carrying the old stamp over would misreport it as live.
        key.LastUsedAt = null;
        key.ModifiedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var datasetNames = await GetDatasetNamesAsync(companyId);
        return new CreateApiKeyResult { Key = ToDto(key, datasetNames), PlainTextKey = rawKey };
    }

    public async Task<bool> RevokeKeyAsync(string companyId, string id)
    {
        var key = await _db.ApiKey.FirstOrDefaultAsync(k => k.CompanyId == companyId && k.Id == id);
        if (key == null) return false;
        if (key.RevokedAt == null)
        {
            key.RevokedAt = DateTime.UtcNow;
            key.ModifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return true;
    }

    public async Task<bool> DeleteKeyAsync(string companyId, string id)
    {
        var key = await _db.ApiKey
            .Where(k => k.CompanyId == companyId && k.Id == id)
            .Include(k => k.Scopes)
            .FirstOrDefaultAsync();
        if (key == null) return false;

        // No cascade deletes in this DB; remove children explicitly.
        _db.ApiKeyScope.RemoveRange(key.Scopes);
        _db.ApiKey.Remove(key);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<ApiKey?> ValidateAsync(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return null;

        var hash = Hash(rawKey.Trim());
        var key = await _db.ApiKey
            .Where(k => k.KeyHash == hash)
            .Include(k => k.Scopes)
            .FirstOrDefaultAsync();

        if (key == null || !key.IsActive) return null;

        // Best-effort last-used stamp; never fail the request over it.
        try
        {
            key.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch { /* ignore */ }

        return key;
    }

    public bool IsInScope(ApiKey key, string datasetId, string? tableName, ApiKeyOperation operation)
    {
        if (key?.Scopes == null) return false;

        return key.Scopes.Any(s =>
            string.Equals(s.DatasetId, datasetId, StringComparison.Ordinal)
            && (string.IsNullOrEmpty(s.TableName)
                || string.IsNullOrEmpty(tableName)
                || string.Equals(s.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            && (operation == ApiKeyOperation.Read ? s.CanRead : s.CanImport));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static (string rawKey, string prefix, string hash) GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeySecretBytes);
        var token = Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var rawKey = KeyPrefixTag + token;
        var prefix = rawKey.Length <= DisplayPrefixLength ? rawKey : rawKey.Substring(0, DisplayPrefixLength);
        return (rawKey, prefix, Hash(rawKey));
    }

    // Random 256-bit keys have full entropy, so a plain SHA-256 (not a slow password hash) is the
    // right fit for constant-cost lookup. We store the base64 digest and match on it.
    private static string Hash(string rawKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToBase64String(digest);
    }

    private List<ApiKeyScope> BuildScopes(string apiKeyId, List<ApiKeyScopeDto>? requested, HashSet<string> validDatasetIds)
    {
        var scopes = new List<ApiKeyScope>();
        if (requested == null) return scopes;

        // Collapse duplicates on (dataset, table) so the same grant can't be listed twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in requested)
        {
            if (string.IsNullOrWhiteSpace(dto.DatasetId)) continue;
            if (!validDatasetIds.Contains(dto.DatasetId)) continue; // only datasets owned by this company
            if (!dto.CanRead && !dto.CanImport) continue;            // a grant with no permission is meaningless

            var table = string.IsNullOrWhiteSpace(dto.TableName) ? null : dto.TableName.Trim();
            var dedupeKey = $"{dto.DatasetId}::{table ?? "*"}";
            if (!seen.Add(dedupeKey)) continue;

            scopes.Add(new ApiKeyScope
            {
                Id = Guid.NewGuid().ToString(),
                ApiKeyId = apiKeyId,
                DatasetId = dto.DatasetId,
                TableName = table,
                CanRead = dto.CanRead,
                CanImport = dto.CanImport,
                CreatedAt = DateTime.UtcNow,
            });
        }
        return scopes;
    }

    private async Task<HashSet<string>> GetCompanyDatasetIdsAsync(string companyId)
    {
        var ids = await _db.Dataset
            .Where(d => d.CompanyId == companyId)
            .Select(d => d.Id!)
            .ToListAsync();
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, string>> GetDatasetNamesAsync(string companyId)
    {
        return await _db.Dataset
            .Where(d => d.CompanyId == companyId)
            .ToDictionaryAsync(d => d.Id!, d => d.Name ?? d.Id!);
    }

    private static ApiKeyDto ToDto(ApiKey key, Dictionary<string, string> datasetNames) => new()
    {
        Id = key.Id,
        Name = key.Name,
        KeyPrefix = key.KeyPrefix,
        ExpiresAt = key.ExpiresAt,
        RevokedAt = key.RevokedAt,
        LastUsedAt = key.LastUsedAt,
        CreatedAt = key.CreatedAt,
        IsActive = key.IsActive,
        Scopes = (key.Scopes ?? new List<ApiKeyScope>()).Select(s => new ApiKeyScopeDto
        {
            DatasetId = s.DatasetId,
            DatasetName = datasetNames.TryGetValue(s.DatasetId, out var name) ? name : s.DatasetId,
            TableName = s.TableName,
            CanRead = s.CanRead,
            CanImport = s.CanImport,
        }).ToList(),
    };
}

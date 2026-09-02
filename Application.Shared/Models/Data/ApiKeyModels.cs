using System;
using System.Collections.Generic;

namespace Application.Shared.Models.Data;

/// <summary>A single dataset/table grant, used in both requests and responses (no secret).</summary>
public class ApiKeyScopeDto
{
    public string DatasetId { get; set; } = string.Empty;
    public string? DatasetName { get; set; }
    // Null/empty = all tables in the dataset.
    public string? TableName { get; set; }
    public bool CanRead { get; set; } = true;
    public bool CanImport { get; set; }
}

/// <summary>A key as returned to the management UI — never carries the secret or its hash.</summary>
public class ApiKeyDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public List<ApiKeyScopeDto> Scopes { get; set; } = new();
}

public class CreateApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public List<ApiKeyScopeDto> Scopes { get; set; } = new();
}

public class UpdateApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public List<ApiKeyScopeDto> Scopes { get; set; } = new();
}

/// <summary>Returned by create and rotate — the one time the raw key is revealed.</summary>
public class CreateApiKeyResult
{
    public ApiKeyDto Key { get; set; } = new();
    public string PlainTextKey { get; set; } = string.Empty;
}

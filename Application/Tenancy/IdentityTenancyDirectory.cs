using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Application.Tenancy;

/// <summary>
/// Company membership, read from the Flowbyte identity application's <c>api/tenancy</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaced.</b> Applications here used to carry their own <c>company</c>, <c>company_member</c>
/// and <c>application_user</c> tables, and a DbContext that could be pointed at either the application's
/// catalog or identity's with a read-only SQL login. It worked, but it meant two applications
/// shared a schema: identity could not rename a column without breaking a deployment it had no
/// visibility of, and each carried a guard in code to stop itself writing to a live application's
/// database. The tables are gone and this is what took their place.
/// </para>
/// <para>
/// <b>Cached briefly, and only on success.</b> The tenant is resolved on every page load and every
/// hub connection, so this cannot be an outbound request each time. Two minutes is short enough
/// that a membership granted in identity appears without anybody signing out, and long enough that
/// a burst of requests costs one call. A failure is never cached — an identity that comes back in
/// ten seconds should be usable in ten seconds.
/// </para>
/// <para>
/// <b>A failure reads as no memberships.</b> That is what the database version did, and the
/// callers already say something sensible about it. It is logged as an error rather than swallowed,
/// because "identity is unreachable" and "this person genuinely belongs to nothing" produce the
/// same empty list downstream and only the log can tell them apart.
/// </para>
/// </remarks>
public sealed class IdentityTenancyDirectory : ITenancyDirectory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IdentityServiceTokens _tokens;
    private readonly IdentityServiceClient _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IdentityTenancyDirectory> _logger;

    public IdentityTenancyDirectory(
        IHttpClientFactory httpClientFactory,
        IdentityServiceTokens tokens,
        IdentityServiceClient client,
        IMemoryCache cache,
        ILogger<IdentityTenancyDirectory> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Company>> GetCompaniesAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return [];
        }

        var key = $"tenancy:companies:{userId}";

        if (_cache.TryGetValue<IReadOnlyList<Company>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var companies = await FetchAsync(userId, ct);

        if (companies is null)
        {
            // Not cached: the next request tries again rather than inheriting this one's outage.
            return [];
        }

        if (companies.Count == 0)
        {
            _logger.LogInformation(
                "Identity lists no active company membership for {UserId}. Membership is granted in "
                + "the identity application.", userId);
        }

        _cache.Set(key, companies, CacheFor);

        return companies;
    }

    public async Task<bool> IsMemberAsync(string userId, string companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(companyId))
        {
            return false;
        }

        var companies = await GetCompaniesAsync(userId, ct);

        return companies.Any(c => string.Equals(c.Id, companyId, StringComparison.Ordinal));
    }

    /// <summary>The companies, or null when identity could not be asked.</summary>
    private async Task<IReadOnlyList<Company>?> FetchAsync(string userId, CancellationToken ct)
    {
        var path = $"api/tenancy/users/{Uri.EscapeDataString(userId)}/companies";

        try
        {
            var first = await SendAsync(path, ct);

            // A token can stop being accepted before it stops being valid — identity's signing key
            // is a file, and replacing it invalidates everything signed with the old one. One
            // retry with a fresh token turns that from an outage into a hiccup.
            if (first is { StatusCode: HttpStatusCode.Unauthorized })
            {
                first.Dispose();
                _tokens.Invalidate();
                _logger.LogInformation("Identity rejected the service token; retrying with a fresh one.");
                first = await SendAsync(path, ct);
            }

            if (first is null)
            {
                return null;
            }

            using var response = first;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Identity's tenancy API returned {StatusCode} for {UserId}: {Body}",
                    (int)response.StatusCode, userId, await response.Content.ReadAsStringAsync(ct));

                return null;
            }

            var memberships = await response.Content.ReadFromJsonAsync<List<MembershipDto>>(ct);

            return memberships?
                   .Where(m => !string.IsNullOrEmpty(m.Id))
                   .Select(m => new Company { Id = m.Id, Name = m.Name ?? m.Id })
                   .ToList()
                ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "Could not read memberships for {UserId} from {BaseUrl}.", userId, _client.BaseUrl);
            return null;
        }
    }

    /// <summary>One GET with the service token, or null when there is no token to send.</summary>
    private async Task<HttpResponseMessage?> SendAsync(string path, CancellationToken ct)
    {
        var token = await _tokens.GetAsync(ct);

        if (token is null)
        {
            // IdentityServiceTokens has already logged why, with identity's own error text.
            return null;
        }

        // Not disposed, deliberately: the caller reads the response after this returns, and a
        // factory client owns no connection to release — the handler it borrows is pooled.
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{_client.BaseUrl}/{path}"));
        request.Bearer(token);

        return await http.SendAsync(request, ct);
    }

    /// <summary>Identity's wire shape. Mirrored rather than shared: it is a contract, not a type.</summary>
    private sealed class MembershipDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }
}

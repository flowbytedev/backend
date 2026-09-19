using Application.Tenancy;
using Application.Models;
using Application.Shared.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Application.Services;

/// <summary>
/// Fetches launcher tiles from the Flowbyte identity application's API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity owns the app registry.</b> The alternative was to keep reading <c>dbo.application</c>
/// directly, which is what the other apps in the estate do. That table is shared, so a second
/// reader is a second thing to coordinate with whenever its schema moves — including the pending
/// migration of <c>icon</c> from Font Awesome class strings to Lucide names. Going through the API
/// means this application is unaffected by that change.
/// </para>
/// <para>
/// <b>It authenticates as this application, not as the user.</b> Identity's
/// <c>api/applauncher/apps</c> used
/// to resolve the caller from its own sign-in cookie, which this application does not have in a
/// server-to-server call — so this returned no tiles and said so once per cache window, and the
/// launcher rendered empty. Identity accepts a service token with the <c>launcher.read</c> scope
/// now, and names the user in the query string; the same token that reads company membership.
/// </para>
/// <para>
/// Results are cached briefly. The registry changes perhaps monthly, and a launcher rendered on
/// every page load should not mean an outbound request on every page load.
/// </para>
/// </remarks>
public sealed class IdentityAppLauncherClient : IAppLauncherClient
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IdentityServiceTokens _tokens;
    private readonly IdentityServiceClient _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IdentityAppLauncherClient> _logger;

    public IdentityAppLauncherClient(
        IHttpClientFactory httpClientFactory,
        IdentityServiceTokens tokens,
        IdentityServiceClient client,
        IMemoryCache cache,
        ILogger<IdentityAppLauncherClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AppTile>> GetTilesAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return [];
        }

        var cacheKey = $"applauncher:{userId}";

        if (_cache.TryGetValue<IReadOnlyList<AppTile>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var tiles = await FetchAsync(userId, cancellationToken);

        // Cached even when empty, so that an identity outage costs one request per window rather
        // than one per page load. A launcher is decoration; it is not worth retrying hard for.
        _cache.Set(cacheKey, tiles, CacheFor);

        return tiles;
    }

    private async Task<IReadOnlyList<AppTile>> FetchAsync(string userId, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAsync(cancellationToken);

        if (token is null)
        {
            // IdentityServiceTokens has already logged why, with identity's own error text.
            return [];
        }

        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"{_client.BaseUrl}/api/applauncher/apps?userId={Uri.EscapeDataString(userId)}"));

            request.Bearer(token);

            using var response = await http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Identity app launcher returned {StatusCode}; rendering the launcher empty.",
                    (int)response.StatusCode);
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<Response<List<AppTile>>>(cancellationToken);

            return payload?.Items ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A launcher is decoration. It must never take the page down with it.
            _logger.LogWarning(ex, "Could not reach the identity app launcher; rendering it empty.");
            return [];
        }
    }
}

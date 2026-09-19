using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Application.Tenancy;

/// <summary>
/// This application's own access token for identity's API — the one it holds as itself, with no user.
/// </summary>
/// <remarks>
/// <para>
/// The OAuth 2.0 client credentials grant (RFC 6749 §4.4). The application needs to read company membership
/// at moments when nobody is holding a browser open — a SignalR hub reconnecting, a driver's
/// bearer request, a Blazor circuit hours into its life — so the token cannot be a user's.
/// </para>
/// <para>
/// <b>The application is a confidential client here and a public one at sign-in, and that is deliberate.</b>
/// Signing a person in uses PKCE and no secret, because the exchange happens in a browser the user
/// controls. This exchange happens between two servers, so a secret is the appropriate proof and
/// there is no browser to leak it to. Identity keeps only its hash.
/// </para>
/// <para>
/// Cached until shortly before it expires, and fetched behind a lock so that a cold start with
/// twenty concurrent requests produces one token rather than twenty.
/// </para>
/// </remarks>
public sealed class IdentityServiceTokens : IDisposable
{
    /// <summary>
    /// Renewed this long before expiry, so a token is never spent on a request it cannot finish.
    /// </summary>
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IdentityServiceClient _client;
    private readonly ILogger<IdentityServiceTokens> _logger;

    /// <summary>
    /// The token and its renewal moment, as one value.
    /// </summary>
    /// <remarks>
    /// One volatile reference rather than two fields, because this is read outside the lock by
    /// every caller. Two fields could be seen half-updated — a fresh token with a stale expiry, or
    /// the reverse — and the second of those hands out a token that has already expired.
    /// </remarks>
    private volatile Lease? _lease;

    public IdentityServiceTokens(
        IHttpClientFactory httpClientFactory,
        IdentityServiceClient client,
        ILogger<IdentityServiceTokens> logger)
    {
        _httpClientFactory = httpClientFactory;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// A usable bearer token, or null when identity would not issue one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: the callers of this degrade — an empty launcher, an empty
    /// company list — and a thrown exception here would take a page down over a service that is
    /// merely unreachable.
    /// </remarks>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (_lease is { } current && DateTimeOffset.UtcNow < current.RenewAt)
        {
            return current.Token;
        }

        await _gate.WaitAsync(ct);

        try
        {
            // Another caller may have renewed it while this one waited.
            if (_lease is { } renewed && DateTimeOffset.UtcNow < renewed.RenewAt)
            {
                return renewed.Token;
            }

            return await FetchAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Throws away the cached token, so the next <see cref="GetAsync"/> fetches a fresh one.
    /// </summary>
    /// <remarks>
    /// For a caller that has just been given a 401 by identity. A token can stop being accepted
    /// before it stops being valid — identity's signing key is rotated by replacing a file — and
    /// without this this application would keep presenting the same rejected token until it expired.
    /// </remarks>
    public void Invalidate() => _lease = null;

    private async Task<string?> FetchAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _client.ClientId,
            ["client_secret"] = _client.ClientSecret,
            ["scope"] = _client.Scope,
        };

        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            using var response = await http.PostAsync(
                new Uri($"{_client.BaseUrl}/connect/token"), new FormUrlEncodedContent(form), ct);

            if (!response.IsSuccessStatusCode)
            {
                // The body carries identity's OAuth error — invalid_client for a wrong secret,
                // invalid_scope for a grant that was never made. Both are configuration, and
                // both are unreadable from a bare status code.
                _logger.LogError(
                    "Identity refused the service token ({StatusCode}): {Body}",
                    (int)response.StatusCode,
                    await response.Content.ReadAsStringAsync(ct));

                return null;
            }

            var issued = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);

            if (issued?.AccessToken is not { Length: > 0 } token)
            {
                _logger.LogError("Identity's token endpoint returned no access_token.");
                return null;
            }

            // Never negative: a token whose lifetime is shorter than the renewal margin is
            // renewed on every call rather than treated as already expired.
            _lease = new Lease(token, DateTimeOffset.UtcNow
                + TimeSpan.FromSeconds(Math.Max(issued.ExpiresIn, 0)) - Early);

            _logger.LogInformation(
                "Service token issued to {ClientId} for [{Scope}], good for {Seconds}s.",
                _client.ClientId, issued.Scope ?? _client.Scope, issued.ExpiresIn);

            return token;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Could not reach identity's token endpoint at {BaseUrl}.", _client.BaseUrl);
            return null;
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>A token and the moment to stop using it.</summary>
    private sealed record Lease(string Token, DateTimeOffset RenewAt);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}

/// <summary>Who this application is to identity when it calls as itself.</summary>
/// <param name="BaseUrl">Identity's https address, no trailing slash.</param>
/// <param name="ClientId">This application's row in identity's <c>dbo.application</c>.</param>
/// <param name="ClientSecret">The secret whose SHA-256 is in that row's <c>client_secret_hash</c>.</param>
/// <param name="Scope">What this application asks for. One scope today, space-separated if that changes.</param>
public sealed record IdentityServiceClient(string BaseUrl, string ClientId, string ClientSecret, string Scope)
{
    /// <summary>Reading companies and memberships. Identity's names for these, not the application's.</summary>
    public const string TenancyRead = "tenancy.read";

    /// <summary>Reading a named user's app launcher tiles.</summary>
    public const string LauncherRead = "launcher.read";

    /// <summary>
    /// Everything this application needs. Identity refuses anything its application row was not
    /// granted, so this and the scopes ticked in identity's Applications screen must agree.
    /// </summary>
    public const string DefaultScopes = TenancyRead + " " + LauncherRead;

    /// <summary>
    /// Reads the secret and the scope, and refuses to start without the secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base URL and client id are passed in rather than read here, because sign-in already
    /// reads and validates them and one reader of a setting is enough. It is the same application
    /// row either way: one registration, two grants.
    /// </para>
    /// <para>
    /// A missing secret is not a degraded application, it is a useless one: without it identity
    /// issues no token, no memberships can be read, and every page says the user belongs to no
    /// company.
    /// That dead end was already diagnosed once from the far end; better to name it here.
    /// </para>
    /// </remarks>
    public static IdentityServiceClient For(string baseUrl, string clientId, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var secret = configuration["Identity:ClientSecret"];

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Identity:ClientSecret is not set. This application reads company membership and launcher tiles "
                + "from the identity application's API and authenticates to it with the OAuth client "
                + "credentials grant, so without this nobody belongs to any company. Put the secret "
                + "whose SHA-256 is in identity's dbo.application.client_secret_hash for this client "
                + "in Application/appsettings.json — generate the pair with db/005_service_clients.sql "
                + "in the identity repository. Note that a user-secret of the same name overrides "
                + "that file in Development.");
        }

        return new IdentityServiceClient(
            baseUrl,
            clientId,
            secret,
            configuration["Identity:Scope"] ?? DefaultScopes);
    }
}

/// <summary>Adds the service token to an outbound request.</summary>
public static class ServiceTokenExtensions
{
    public static void Bearer(this HttpRequestMessage request, string token)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

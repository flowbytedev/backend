using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// OAuth2 client-credentials support: fetch an access token from the credential's token endpoint, cache it
/// until it expires, and hand it to every request that credential authenticates.
/// <para>
/// In its own file because it is the only part of the client that makes a request of its own rather than the
/// one it was asked for, and because it is the only part holding process-wide state.
/// </para>
/// <para>
/// The alternative — a token request as pipeline steps — leaks the token into
/// <c>pipeline_run_step.output_preview_json</c> in the clear for the retention window, ignores
/// <c>expires_in</c> so every run fetches again, and has to be repeated in every pipeline that touches the
/// API. None of that is visible while building it, which is why it is worth stating here.
/// </para>
/// </summary>
public partial class PipelineApiClient
{
    /// <summary>
    /// Cached tokens, keyed by credential id plus a fingerprint of what was used to obtain the token.
    /// <para>
    /// Static, so one process fetches a token once however many pipelines and steps want it. Per-process
    /// rather than shared: the web app and the scheduler each keep their own, which costs one extra token
    /// request and avoids putting a bearer token in a shared store.
    /// </para>
    /// <para>
    /// The fingerprint is what makes editing a credential safe. Keyed on id alone, changing the scope or the
    /// secret would keep serving the token fetched under the old settings until it expired.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, CachedToken> TokenCache = new(StringComparer.Ordinal);

    /// <summary>
    /// One gate per cache key, so a burst of parallel source fetches produces one token request rather than
    /// one each. <c>SourceFetchConcurrency</c> defaults to 3, so without this a cold cache means three
    /// simultaneous token requests — which some providers rate-limit.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TokenGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Refresh this long before the stated expiry. Covers clock skew and the time the request itself takes;
    /// without it a token fetched with one second left is valid when checked and expired on arrival.
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Used when the token response omits <c>expires_in</c>. Short on purpose: guessing long means serving a
    /// dead token for an hour, and the cost of guessing short is one extra token request.
    /// </summary>
    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(5);

    private sealed record CachedToken(string Token, DateTime ExpiresAtUtc)
    {
        public bool IsUsable => DateTime.UtcNow < ExpiresAtUtc;
    }

    /// <summary>
    /// The access token for a credential, from cache when it is still good. Returns the error rather than
    /// throwing, matching everything else here.
    /// </summary>
    private async Task<(string? Token, string? Error)> AccessTokenAsync(
        ResolvedApiCredential credential, bool forceRefresh, CancellationToken ct)
    {
        var key = CacheKey(credential);

        if (!forceRefresh && TokenCache.TryGetValue(key, out var cached) && cached.IsUsable)
            return (cached.Token, null);

        var gate = TokenGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            // Re-checked inside the gate: whoever was ahead in the queue has just refreshed it, and fetching
            // again would defeat the point of serialising.
            if (!forceRefresh && TokenCache.TryGetValue(key, out cached) && cached.IsUsable)
                return (cached.Token, null);

            var fetched = await FetchTokenAsync(credential, ct);
            if (fetched.Error is not null) return (null, fetched.Error);

            TokenCache[key] = new CachedToken(fetched.Token!, DateTime.UtcNow + fetched.Lifetime);
            return (fetched.Token, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops a credential's cached token. Called when the API answers 401, because the most likely reason is
    /// a token the provider considers dead even though our clock says otherwise.
    /// </summary>
    private static void InvalidateToken(ResolvedApiCredential credential) =>
        TokenCache.TryRemove(CacheKey(credential), out _);

    /// <summary>
    /// Cache key: the credential id plus a fingerprint of everything that determines which token comes back.
    /// The secret is hashed, never stored — a key sitting in a static dictionary is not a place for one.
    /// </summary>
    private static string CacheKey(ResolvedApiCredential credential)
    {
        var c = credential.Credential;

        // A control character as the separator, written as an escape so it is visible in source: without a
        // separator, "ab" + "c" and "a" + "bc" would fingerprint identically.
        var material = string.Join('\u001f',
            c.Id, c.TokenUrl ?? string.Empty, c.TokenFieldsJson ?? string.Empty,
            c.FormFieldName ?? string.Empty, credential.Secret ?? string.Empty);

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material)));

        return $"{c.Id}:{hash}";
    }

    /// <summary>
    /// Posts the token request and reads the access token out of the response.
    /// <para>
    /// Built here rather than through <see cref="SendAsync"/> for two reasons. It must not recurse — that
    /// path would try to authenticate the token request itself. And it must not go through the base-URL host
    /// check, because a token endpoint is nearly always on a different host from the API; that check exists
    /// to stop a <em>step</em> aiming a credential at an arbitrary host, and this URL comes from the
    /// credential rather than from a step.
    /// </para>
    /// </summary>
    private async Task<(string? Token, TimeSpan Lifetime, string? Error)> FetchTokenAsync(
        ResolvedApiCredential credential, CancellationToken ct)
    {
        var c = credential.Credential;

        if (string.IsNullOrWhiteSpace(c.TokenUrl))
            return (null, default, $"The '{c.Name}' credential has no token URL.");

        if (!Uri.TryCreate(c.TokenUrl, UriKind.Absolute, out var tokenUri)
            || (tokenUri.Scheme != Uri.UriSchemeHttp && tokenUri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, default, $"The '{c.Name}' credential's token URL is not a valid http(s) URL.");
        }

        var body = TokenRequestBody(credential);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(
            c.TimeoutSeconds ?? options.ResolveApiTimeoutSeconds()));

        try
        {
            var http = httpClientFactory.CreateClient(ClientNameFor(c));

            using var message = new HttpRequestMessage(HttpMethod.Post, tokenUri);
            message.Content = new StringContent(body, Encoding.UTF8);
            message.Content.Headers.ContentType =
                new MediaTypeHeaderValue(PipelineApiContentTypes.Form) { CharSet = "utf-8" };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(PipelineApiContentTypes.Json));

            using var response = await http.SendAsync(
                message, HttpCompletionOption.ResponseContentRead, cts.Token);

            var text = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                // The provider's own message is the useful part — "invalid_scope" or "unauthorized_client"
                // says exactly what to fix. Truncated, and the request body is never included: it holds the
                // secret.
                return (null, default,
                    $"Could not get a token from {tokenUri.Host} ({(int)response.StatusCode}): "
                    + Truncate(text, 300));
            }

            var parsed = ReadToken(text);

            return parsed.Token is null
                ? (null, default,
                    $"The token response from {tokenUri.Host} contained no access_token.")
                : (parsed.Token, parsed.Lifetime, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, default, $"The token request to {tokenUri.Host} timed out.");
        }
        catch (Exception ex)
        {
            // Describe, not ex.Message: a TLS failure says only "see inner exception", and this is the
            // request it happens to first — see the note on Describe.
            return (null, default,
                $"Could not reach {tokenUri.Host}: {Describe(ex)}" + CertificateHint(c, ex));
        }
    }

    /// <summary>
    /// The form-encoded token request body: the credential's non-secret fields, then the secret.
    /// <para>
    /// The secret goes last so a field of the same name in the JSON cannot displace the encrypted value.
    /// </para>
    /// </summary>
    internal static string TokenRequestBody(ResolvedApiCredential credential)
    {
        var c = credential.Credential;
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(c.TokenFieldsJson))
        {
            try
            {
                if (JsonNode.Parse(c.TokenFieldsJson!) is JsonObject obj)
                {
                    foreach (var (name, value) in obj)
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        parts.Add(FormPair(name, value?.ToString() ?? string.Empty));
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed JSON must not take the run down with a parse error; the credential editor
                // validates it on save, so reaching here means the row was edited by hand. The request will
                // fail at the provider with a message about the missing fields, which is more useful.
            }
        }

        var secretField = string.IsNullOrWhiteSpace(c.FormFieldName) ? "client_secret" : c.FormFieldName!;
        parts.Add(FormPair(secretField, credential.Secret ?? string.Empty));

        return string.Join("&", parts);
    }

    /// <summary>
    /// Pulls <c>access_token</c> and <c>expires_in</c> out of a token response.
    /// <para>
    /// <c>expires_in</c> is seconds per RFC 6749, and providers send it as both a number and a string, so
    /// both are accepted. A missing or unreadable value falls back to a short lifetime rather than assuming
    /// an hour.
    /// </para>
    /// </summary>
    internal static (string? Token, TimeSpan Lifetime) ReadToken(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return (null, default);

        try
        {
            if (JsonNode.Parse(responseBody!) is not JsonObject obj) return (null, default);

            var token = obj["access_token"]?.ToString();
            if (string.IsNullOrWhiteSpace(token)) return (null, default);

            var lifetime = FallbackLifetime;

            if (obj["expires_in"] is JsonValue expires)
            {
                var seconds =
                    expires.TryGetValue<long>(out var asNumber) ? asNumber
                    : expires.TryGetValue<string>(out var asText)
                      && long.TryParse(asText, out var parsed) ? parsed
                    : 0;

                if (seconds > 0)
                {
                    // Never negative: a token with a lifetime under the margin is used immediately and
                    // refetched next time rather than being treated as already expired.
                    var usable = TimeSpan.FromSeconds(seconds) - ExpiryMargin;
                    lifetime = usable > TimeSpan.Zero ? usable : TimeSpan.FromSeconds(seconds);
                }
            }

            return (token, lifetime);
        }
        catch (JsonException)
        {
            return (null, default);
        }
    }

    /// <summary>
    /// The identifying claims of a bearer token, for an error message. <b>Never the token itself.</b>
    /// <para>
    /// A 401 from a correctly-configured client is almost always the token being for the wrong <c>aud</c> —
    /// an access token for Graph is perfectly valid and useless to Dynamics. That single claim separates
    /// "wrong scope" from "application not authorised there", which produce an identical 401 with an empty
    /// body. Printing it turns a guessing game into a reading.
    /// </para>
    /// <para>
    /// <c>aud</c>, <c>appid</c>, <c>tid</c> and <c>exp</c> are identifiers and a timestamp, not credentials —
    /// the signature and the token string are what must never be logged, and neither is included.
    /// </para>
    /// </summary>
    internal static string DescribeToken(string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer)) return " [no token was attached]";

        var parts = bearer!.Split('.');
        if (parts.Length < 2) return " [the token is not a JWT, so its claims cannot be read]";

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            if (JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))
                is not JsonObject claims)
            {
                return " [the token payload is not readable]";
            }

            var described = new List<string>();

            // The audience is the whole point of this message, so it comes first.
            if (claims["aud"]?.ToString() is { Length: > 0 } audience)
                described.Add($"aud={audience}");

            if (claims["appid"]?.ToString() is { Length: > 0 } appId) described.Add($"appid={appId}");
            if (claims["tid"]?.ToString() is { Length: > 0 } tenant) described.Add($"tid={tenant}");

            if (claims["roles"] is JsonArray roles && roles.Count > 0)
                described.Add($"roles={string.Join('|', roles.Select(r => r?.ToString()))}");
            else
                described.Add("roles=(none)");

            if (claims["exp"] is JsonValue exp && exp.TryGetValue<long>(out var expiresAt))
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(expiresAt);
                described.Add(when > DateTimeOffset.UtcNow
                    ? $"expires in {(int)(when - DateTimeOffset.UtcNow).TotalMinutes}m"
                    : "ALREADY EXPIRED");
            }

            return described.Count == 0
                ? " [the token carried no identifying claims]"
                : $" [token: {string.Join(", ", described)}]";
        }
        catch (Exception)
        {
            // A diagnostic must never be the thing that fails the request.
            return " [the token claims could not be decoded]";
        }
    }

    /// <summary>True when a response means "your token is no longer good".</summary>
    private static bool IsUnauthorized(HttpStatusCode status) => status == HttpStatusCode.Unauthorized;
}

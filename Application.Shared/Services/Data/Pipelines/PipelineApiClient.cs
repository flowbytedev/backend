using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Shared.Data;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The one place a pipeline talks HTTP. Owns credential resolution, auth, retry and redirect policy, so the
/// source reader and the destination writer differ only in what they do with the body.
/// </summary>
public interface IPipelineApiClient
{
    /// <summary>
    /// Loads a credential by name or id and decrypts its secret. <paramref name="forWrite"/> additionally
    /// requires <see cref="ApiCredential.AllowWrite"/>.
    /// </summary>
    Task<ResolvedApiCredential?> ResolveAsync(
        string reference, string companyId, bool forWrite, CancellationToken ct = default);

    /// <summary>
    /// Issues one request, applying auth, retrying transient failures, and refusing a cross-origin redirect.
    /// Never throws for an HTTP-level outcome — inspect <see cref="ApiResponse.Success"/>.
    /// </summary>
    Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken ct = default);
}

/// <summary>A credential with its secret decrypted. Never serialized anywhere.</summary>
public sealed class ResolvedApiCredential
{
    public required ApiCredential Credential { get; init; }
    public string? Secret { get; init; }

    public string Name => Credential.Name;
    public string AuthType => Credential.AuthType ?? ApiAuthTypes.None;
}

public sealed class ApiRequest
{
    public required ResolvedApiCredential Credential { get; init; }

    /// <summary>Absolute, or relative to the credential's base URL.</summary>
    public required string Url { get; init; }

    public string Method { get; init; } = "GET";

    /// <summary>Per-step headers, merged over the credential's static ones.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Extra query parameters — this is how pagination is applied.</summary>
    public IReadOnlyDictionary<string, string>? Query { get; init; }

    public string? Body { get; init; }

    /// <summary>
    /// The body's Content-Type. May carry parameters (<c>application/json; charset=utf-8</c>).
    /// <para>
    /// A <c>Content-Type</c> in <see cref="Headers"/> wins over this, because that is where somebody copying
    /// a curl example will put it — and <c>HttpRequestMessage.Headers</c> silently discards content headers,
    /// so honouring it there is the difference between working and quietly doing nothing.
    /// </para>
    /// </summary>
    public string ContentType { get; init; } = PipelineApiContentTypes.Json;

    /// <summary>Overrides the credential's and the server's default.</summary>
    public int? TimeoutSeconds { get; init; }
}

public sealed class ApiResponse
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? Body { get; init; }

    /// <summary>The <c>rel="next"</c> target from the <c>Link</c> header, if any.</summary>
    public string? NextLink { get; init; }

    public string? Error { get; init; }
    public string? ErrorType { get; init; }

    /// <summary>The URL actually requested, with secrets removed — safe to log.</summary>
    public string? SafeUrl { get; init; }

    public static ApiResponse Fail(string error, string errorType, int status = 0, string? safeUrl = null) =>
        new() { Success = false, Error = error, ErrorType = errorType, StatusCode = status, SafeUrl = safeUrl };
}

public class PipelineApiClient(
    ApplicationDbContext db,
    ICredentialProtector protector,
    IHttpClientFactory httpClientFactory,
    PipelineOptions options) : IPipelineApiClient
{
    /// <summary>
    /// Named client configured in both hosts with automatic redirects DISABLED. Redirects are followed here
    /// instead, and only within the same origin — .NET strips the <c>Authorization</c> header when it
    /// redirects across origins, but it does NOT strip a custom header, so an <c>X-Api-Key</c> credential
    /// would be handed to whatever host the redirect names. That is a credential leak triggered by the
    /// remote server rather than by us, which is exactly the kind worth closing.
    /// </summary>
    public const string HttpClientName = "pipeline-api";

    private const int MaxRedirects = 3;

    public async Task<ResolvedApiCredential?> ResolveAsync(
        string reference, string companyId, bool forWrite, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var credential = await db.ApiCredential.AsNoTracking().FirstOrDefaultAsync(
            c => c.CompanyId == companyId && (c.Id == reference || c.Name == reference), ct);

        if (credential is null) return null;

        string? secret = null;
        if (ApiAuthTypes.NeedsSecret(credential.AuthType) && !string.IsNullOrEmpty(credential.SecretEncrypted))
            secret = protector.Decrypt(credential.SecretEncrypted!);

        return new ResolvedApiCredential { Credential = credential, Secret = secret };
    }

    public async Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken ct = default)
    {
        var built = BuildUri(request);
        if (built.Error is not null)
            return ApiResponse.Fail(built.Error, PipelineErrorType.Invalid);

        var uri = built.Uri!;
        var attempts = Math.Max(1, options.ResolveApiRetryAttempts());
        var timeout = request.TimeoutSeconds
                      ?? request.Credential.Credential.TimeoutSeconds
                      ?? options.ResolveApiTimeoutSeconds();

        ApiResponse? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var response = await SendOnceAsync(request, uri, timeout, MaxRedirects, ct);

            // A 4xx other than 429 is a contract problem — the URL, the body or the token is wrong. Retrying
            // sends the same wrong request again, so fail immediately and say what came back.
            if (response.Success || !IsTransient(response.StatusCode))
                return response;

            last = response;

            if (attempt == attempts) break;

            var delay = BackoffFor(attempt, response);
            await Task.Delay(delay, ct);
        }

        return last ?? ApiResponse.Fail("The request could not be sent.", PipelineErrorType.ApiError);
    }

    // ------------------------------------------------------------------ one attempt

    private async Task<ApiResponse> SendOnceAsync(
        ApiRequest request, Uri uri, int timeoutSeconds, int redirectsLeft, CancellationToken ct)
    {
        var safeUrl = Redact(uri, request.Credential);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var message = new HttpRequestMessage(
                new HttpMethod(request.Method.ToUpperInvariant()), uri);

            ApplyHeaders(message, request);

            if (!string.IsNullOrEmpty(request.Body) && message.Method != HttpMethod.Get)
                message.Content = BuildContent(request);

            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);

            // Same-origin redirects only — see the note on HttpClientName.
            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                if (redirectsLeft <= 0)
                    return ApiResponse.Fail(
                        $"The API redirected more than {MaxRedirects} times.",
                        PipelineErrorType.ApiError, (int)response.StatusCode, safeUrl);

                var target = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(uri, response.Headers.Location);

                if (!SameOrigin(uri, target))
                    return ApiResponse.Fail(
                        $"The API redirected to a different host ({target.Host}). This is refused rather "
                        + "than followed, because the credential's header would travel with it.",
                        PipelineErrorType.ApiError, (int)response.StatusCode, safeUrl);

                return await SendOnceAsync(request, target, timeoutSeconds, redirectsLeft - 1, ct);
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new ApiResponse
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    Body = body,
                    SafeUrl = safeUrl,
                    Error = $"The API returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body)}",
                    ErrorType = PipelineErrorType.ApiError
                };
            }

            return new ApiResponse
            {
                Success = true,
                StatusCode = (int)response.StatusCode,
                Body = body,
                NextLink = NextFromLinkHeader(response),
                SafeUrl = safeUrl
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked source fired, not the run's token: this is our own timeout, not a cancellation.
            return ApiResponse.Fail(
                $"The API did not respond within {timeoutSeconds}s.",
                PipelineErrorType.Timeout, 0, safeUrl);
        }
        catch (HttpRequestException ex)
        {
            return ApiResponse.Fail(
                $"The API could not be reached: {ex.Message}", PipelineErrorType.ApiError, 0, safeUrl);
        }
    }

    /// <summary>
    /// The request body, with its Content-Type applied.
    /// <para>
    /// Not <c>new StringContent(body, Encoding.UTF8, contentType)</c>, which is what this used to be: that
    /// overload takes a bare media type and <b>throws</b> on anything carrying a parameter, so a perfectly
    /// ordinary <c>application/json; charset=utf-8</c> typed into the step would have crashed the request
    /// rather than sent it. Parsing the header instead accepts both forms.
    /// </para>
    /// <para>
    /// A charset is added when the author did not give one, because the bytes really are UTF-8 and some
    /// endpoints reject a body that does not say so.
    /// </para>
    /// </summary>
    internal static StringContent BuildContent(ApiRequest request)
    {
        var content = new StringContent(request.Body!, Encoding.UTF8);

        var declared = EffectiveContentType(request);

        if (MediaTypeHeaderValue.TryParse(declared, out var mediaType) && mediaType is not null)
        {
            if (string.IsNullOrWhiteSpace(mediaType.CharSet)) mediaType.CharSet = "utf-8";
            content.Headers.ContentType = mediaType;
        }

        // An unparseable value leaves the StringContent default (text/plain; charset=utf-8) in place rather
        // than failing the send. The endpoint's own error is more useful than ours would be.
        return content;
    }

    /// <summary>
    /// The Content-Type actually used: a <c>Content-Type</c> among the step's extra headers if present,
    /// otherwise the step's own content-type setting, otherwise JSON.
    /// <para>
    /// The header takes precedence deliberately. <c>ApplyHeaders</c> uses
    /// <c>TryAddWithoutValidation</c>, which returns false for a content header rather than throwing — so a
    /// <c>Content-Type</c> typed into "Extra headers" used to be dropped without a word. Somebody
    /// translating a curl command will put it there, and silently ignoring it is the worst of the three
    /// possible behaviours.
    /// </para>
    /// </summary>
    internal static string EffectiveContentType(ApiRequest request)
    {
        if (request.Headers is not null)
        {
            foreach (var (key, value) in request.Headers)
            {
                if (!string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(request.ContentType)
            ? PipelineApiContentTypes.Json
            : request.ContentType.Trim();
    }

    private static void ApplyHeaders(HttpRequestMessage message, ApiRequest request)
    {
        var credential = request.Credential.Credential;

        // Credential-level static headers first, so a step can deliberately override one.
        if (!string.IsNullOrWhiteSpace(credential.ExtraHeadersJson))
        {
            try
            {
                if (JsonNode.Parse(credential.ExtraHeadersJson!) is JsonObject obj)
                    foreach (var (key, value) in obj)
                        if (value is not null)
                            message.Headers.TryAddWithoutValidation(key, value.ToString());
            }
            catch (JsonException)
            {
                // Malformed static headers must not take the run down; the credential editor validates this
                // on save, so reaching here means the row was edited by hand.
            }
        }

        if (request.Headers is not null)
            foreach (var (key, value) in request.Headers)
            {
                message.Headers.Remove(key);
                message.Headers.TryAddWithoutValidation(key, value);
            }

        var secret = request.Credential.Secret;

        switch (request.Credential.AuthType)
        {
            case ApiAuthTypes.Bearer:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;

            case ApiAuthTypes.Basic:
                var pair = $"{credential.Username}:{secret}";
                message.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
                break;

            case ApiAuthTypes.Header:
                if (!string.IsNullOrWhiteSpace(credential.HeaderName))
                {
                    message.Headers.Remove(credential.HeaderName!);
                    message.Headers.TryAddWithoutValidation(credential.HeaderName!, secret);
                }
                break;

            // QueryParam is applied in BuildUri — it is part of the URL, not the headers.
        }
    }

    // ------------------------------------------------------------------ URL building

    private (Uri? Uri, string? Error) BuildUri(ApiRequest request)
    {
        var credential = request.Credential.Credential;
        var baseUrl = credential.BaseUrl;

        Uri? uri;

        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var absolute))
        {
            uri = absolute;

            // A base URL is a boundary, not just a prefix. Without this check a step could point a
            // credential's token at any host by writing a full URL, which is the whole exposure the
            // registered-credential design exists to prevent.
            if (!string.IsNullOrWhiteSpace(baseUrl)
                && Uri.TryCreate(baseUrl, UriKind.Absolute, out var base1)
                && !SameOrigin(base1, uri))
            {
                return (null,
                    $"This step's URL points at {uri.Host}, but the '{credential.Name}' credential is "
                    + $"restricted to {base1.Host}. Use a path relative to the credential's base URL, or "
                    + "register a separate credential for that host.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(baseUrl)
                 && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            // Ensure the base behaves as a directory, so "reports" under ".../v2/" does not replace "v2".
            var normalizedBase = baseUri.AbsoluteUri.EndsWith('/')
                ? baseUri
                : new Uri(baseUri.AbsoluteUri + "/");

            if (!Uri.TryCreate(normalizedBase, request.Url.TrimStart('/'), out uri))
                return (null, $"'{request.Url}' is not a valid path under the credential's base URL.");
        }
        else
        {
            return (null,
                string.IsNullOrWhiteSpace(request.Url)
                    ? "This step has no URL."
                    : $"'{request.Url}' is not an absolute URL, and the '{credential.Name}' credential has "
                      + "no base URL to resolve it against.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (null, $"Only http and https are supported; got '{uri.Scheme}'.");

        return (WithQuery(uri, request), null);
    }

    private static Uri WithQuery(Uri uri, ApiRequest request)
    {
        var credential = request.Credential.Credential;

        var extras = new List<KeyValuePair<string, string>>();
        if (request.Query is not null) extras.AddRange(request.Query);

        if (request.Credential.AuthType == ApiAuthTypes.QueryParam
            && !string.IsNullOrWhiteSpace(credential.QueryParamName))
        {
            extras.Add(new(credential.QueryParamName!, request.Credential.Secret ?? string.Empty));
        }

        if (extras.Count == 0) return uri;

        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');

        foreach (var (key, value) in extras)
        {
            var pair = Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
            query = string.IsNullOrEmpty(query) ? pair : query + "&" + pair;
        }

        builder.Query = query;
        return builder.Uri;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>
    /// The URL with a query-parameter secret masked, for logs and error messages. Query strings end up in
    /// run logs and step error text, which are read by more people than hold the credential.
    /// </summary>
    private static string Redact(Uri uri, ResolvedApiCredential credential)
    {
        var name = credential.Credential.QueryParamName;
        if (credential.AuthType != ApiAuthTypes.QueryParam || string.IsNullOrWhiteSpace(name))
            return uri.GetLeftPart(UriPartial.Path) + uri.Query;

        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return uri.GetLeftPart(UriPartial.Path);

        var parts = query.Split('&').Select(part =>
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            return Uri.UnescapeDataString(key).Equals(name, StringComparison.OrdinalIgnoreCase)
                ? key + "=***"
                : part;
        });

        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", parts);
    }

    // ------------------------------------------------------------------ retry policy

    /// <summary>
    /// Worth retrying: rate limiting, and the 5xx family plus the transport-level failures that surface as
    /// status 0. Everything else is a request this side got wrong.
    /// </summary>
    private static bool IsTransient(int status) =>
        status == 0 || status == 429 || status >= 500;

    private static TimeSpan BackoffFor(int attempt, ApiResponse response)
    {
        // Exponential with a ceiling: 1s, 2s, 4s, 8s, capped at 30s.
        var seconds = Math.Min(30, Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        (int)status is 301 or 302 or 303 or 307 or 308;

    // ------------------------------------------------------------------ Link header

    /// <summary>
    /// Extracts <c>rel="next"</c> from an RFC-5988 <c>Link</c> header:
    /// <c>&lt;https://api/x?page=2&gt;; rel="next", &lt;…&gt;; rel="last"</c>
    /// </summary>
    private static string? NextFromLinkHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;

        foreach (var header in values)
        {
            foreach (var candidate in SplitLinkHeader(header))
            {
                var segments = candidate.Split(';');
                if (segments.Length < 2) continue;

                var target = segments[0].Trim();
                if (!target.StartsWith('<') || !target.EndsWith('>')) continue;

                var isNext = segments.Skip(1).Any(s =>
                {
                    var t = s.Trim().Replace("\"", string.Empty).Replace(" ", string.Empty);
                    return t.Equals("rel=next", StringComparison.OrdinalIgnoreCase);
                });

                if (isNext) return target[1..^1];
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a Link header on commas that separate entries, not on commas inside a &lt;…&gt; target — a
    /// cursor token is base64 and quite capable of containing one.
    /// </summary>
    private static IEnumerable<string> SplitLinkHeader(string header)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    yield return header[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (start < header.Length) yield return header[start..];
    }

    private static string Truncate(string? value, int max = 500) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max] + "…";
}

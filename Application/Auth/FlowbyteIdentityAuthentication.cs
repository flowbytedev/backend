using System.Security.Claims;
using Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Application.Auth;

/// <summary>
/// Sign-in through the Flowbyte identity application.
/// </summary>
/// <remarks>
/// <para>
/// Identity is an OAuth 2.0 authorization server — authorization code with PKCE, signed JWT access
/// tokens, a JWKS — and not (yet) an OpenID provider: its token response carries no
/// <c>id_token</c>. So this application does not use the OpenID Connect handler, which would refuse that
/// response. It uses the generic OAuth handler, and takes the user from the access token instead,
/// after validating the token's signature against identity's published keys. Same trust, different
/// envelope.
/// </para>
/// <para>
/// <b>Public client.</b> Identity authenticates the exchange with PKCE alone
/// (<c>token_endpoint_auth_methods_supported: ["none"]</c>). The OAuth handler insists on a
/// non-empty <c>ClientSecret</c> by validation, so a fixed placeholder is set; identity's token
/// endpoint does not bind that form field and ignores it. There is therefore no secret to rotate,
/// leak or configure — which is one of the reasons to prefer this over Entra direct.
/// </para>
/// <para>
/// What identity must know about this application: an application row whose id is <c>Identity:ClientId</c>
/// (defaults to <c>ApplicationId</c>) with the exact https callback URL in its
/// registered redirect URIs, and access granted to each user. Both are administered in identity.
/// </para>
/// </remarks>
public static class FlowbyteIdentityAuthentication
{
    public const string DefaultCallbackPath = "/signin-identity";

    /// <summary>Sent because the handler requires one; identity never reads it.</summary>
    private const string PublicClientPlaceholder = "public-client";

    public static AuthenticationBuilder AddFlowbyteIdentity(this AuthenticationBuilder auth, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = IdentitySettings.From(configuration);

        auth.Services.AddSingleton(settings);
        auth.Services.AddSingleton<IdentityTokenValidator>();

        return auth.AddOAuth(AuthenticationSchemes.Identity, displayName: "Flowbyte Identity", options =>
        {
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

            options.AuthorizationEndpoint = $"{settings.BaseUrl}/connect/authorize";
            options.TokenEndpoint = $"{settings.BaseUrl}/connect/token";
            options.ClientId = settings.ClientId;
            options.ClientSecret = PublicClientPlaceholder;
            options.CallbackPath = settings.CallbackPath;

            options.UsePkce = true;
            options.SaveTokens = true; // the refresh token is revoked on sign-out

            options.ClaimsIssuer = settings.BaseUrl;

            options.Events = new OAuthEvents
            {
                OnCreatingTicket = async context =>
                {
                    var validator = context.HttpContext.RequestServices.GetRequiredService<IdentityTokenValidator>();

                    var claims = await validator.ValidateAsync(
                        context.AccessToken ?? throw new InvalidOperationException("Identity returned no access token."),
                        context.HttpContext.RequestAborted);

                    context.Principal = new ClaimsPrincipal(IdentityClaims.Build(claims, AuthenticationSchemes.Identity));
                },

                OnRemoteFailure = context =>
                {
                    // In Development the exception page is the most useful thing to show. Anywhere
                    // else, a user who declined or lost access should land somewhere sensible.
                    if (context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
                    {
                        return Task.CompletedTask;
                    }

                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Application.Auth")
                        .LogWarning("Sign-in through identity failed: {Reason}", context.Failure?.Message);

                    context.Response.Redirect("/Error");
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });
    }
}

/// <summary>Where identity is and who this application is to it.</summary>
public sealed record IdentitySettings(string BaseUrl, string ClientId, string CallbackPath)
{
    public string MetadataAddress => $"{BaseUrl}/.well-known/openid-configuration";

    public static IdentitySettings From(IConfiguration configuration)
    {
        var baseUrl = configuration["Identity:BaseUrl"]?.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Identity:BaseUrl must be the https address of the Flowbyte identity application, e.g. https://identity.gmrlapp.com. "
                + "This application signs in through it and has no other way in.");
        }

        var clientId = configuration["Identity:ClientId"] ?? configuration["ApplicationId"];

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Identity:ClientId (or ApplicationId) must name the application's application row in identity.");
        }

        return new IdentitySettings(baseUrl, clientId, configuration["Identity:CallbackPath"] ?? FlowbyteIdentityAuthentication.DefaultCallbackPath);
    }
}

/// <summary>
/// Validates an identity access token against identity's published keys and returns its claims.
/// </summary>
/// <remarks>
/// The token arrived over the back channel, straight from identity over TLS, so the validation is
/// defence in depth rather than the only line — but it is cheap, it catches a misconfigured base
/// URL pointing at the wrong server, and it is the same check the driver API performs on bearer
/// tokens. Keys come from the discovery document and refresh once on a signature failure, which
/// is how key rotation passes without a restart.
/// </remarks>
public sealed class IdentityTokenValidator
{
    private readonly IdentitySettings _settings;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public IdentityTokenValidator(IdentitySettings settings)
    {
        _settings = settings;
        _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
            settings.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public async Task<IEnumerable<Claim>> ValidateAsync(string token, CancellationToken ct)
    {
        var result = await ValidateOnceAsync(token, ct);

        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException)
        {
            // Rotated keys: fetch the document again and try once more.
            _metadata.RequestRefresh();
            result = await ValidateOnceAsync(token, ct);
        }

        if (!result.IsValid)
        {
            throw new InvalidOperationException(
                $"The access token from {_settings.BaseUrl} did not validate: {result.Exception?.Message ?? "unknown reason"}",
                result.Exception);
        }

        return result.ClaimsIdentity.Claims;
    }

    private async Task<TokenValidationResult> ValidateOnceAsync(string token, CancellationToken ct)
    {
        var configuration = await _metadata.GetConfigurationAsync(ct);

        return await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _settings.BaseUrl,
            ValidAudience = _settings.ClientId,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });
    }
}

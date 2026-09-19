using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Application.Auth;

/// <summary>
/// Sign-in and sign-out, in two endpoints.
/// </summary>
/// <remarks>
/// What this replaces is the point: the ASP.NET Identity scaffolding shipped roughly forty Razor
/// components for login, registration, email confirmation, password reset, two-factor enrolment,
/// recovery codes and personal-data download. This application owns no credentials — it signs in through the
/// Flowbyte identity application — so all any of that reduces to is "hand the user to identity"
/// and "drop the cookie".
/// <para>
/// The paths stay <c>/Account/Login</c> and <c>/Account/Logout</c> because
/// <c>RedirectToLogin.razor</c> and existing bookmarks point at them.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    public static IEndpointConventionBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/Account");

        group.MapGet("/Login", (string? returnUrl) =>
            TypedResults.Challenge(
                new AuthenticationProperties { RedirectUri = Local(returnUrl) },
                [AuthenticationSchemes.Identity]))
            .AllowAnonymous();

        // POST, so that a prefetch or an <img> tag cannot sign somebody out. The form carries an
        // antiforgery token and UseAntiforgery validates it; signing out is state-changing.
        //
        // Only the cookie is signed out. Identity has no end-session endpoint, so its own session
        // stays — the next visit to /Account/Login signs back in silently, which is how every other
        // Flowbyte application behaves and what a shared sign-in is for. The refresh token this
        // application was holding is revoked so it cannot be used again.
        group.MapPost("/Logout", async (HttpContext http, [FromForm] string? returnUrl, IHttpClientFactory httpClients, IdentitySettings identity, ILoggerFactory loggers) =>
        {
            var refreshToken = await http.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "refresh_token");

            if (!string.IsNullOrEmpty(refreshToken))
            {
                try
                {
                    using var client = httpClients.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(5);

                    await client.PostAsync($"{identity.BaseUrl}/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["refresh_token"] = refreshToken,
                        ["client_id"] = identity.ClientId,
                    }));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Best effort. The cookie goes regardless; the token expires on its own.
                    loggers.CreateLogger("Application.Auth").LogWarning("Could not revoke the refresh token at sign-out: {Reason}", ex.Message);
                }
            }

            return TypedResults.SignOut(
                new AuthenticationProperties { RedirectUri = SignedOut(returnUrl) },
                [CookieAuthenticationDefaults.AuthenticationScheme]);
        })
        .RequireAuthorization();

        return group;
    }

    /// <summary>
    /// Where to land after signing out: the signed-out page, never back into the application.
    /// </summary>
    /// <remarks>
    /// Returning the user to the page they came from makes sign-out look broken. They arrive
    /// unauthenticated, the router bounces them to identity, identity's own session is still live,
    /// and it signs them straight back in with no prompt — so the only visible effect of pressing
    /// "sign out" is a flicker. The company is carried across so that signing in again lands in
    /// the tenant they were working in.
    /// </remarks>
    private static string SignedOut(string? returnUrl)
    {
        var company = Company(returnUrl);

        return company is null
            ? "/Account/Logout"
            : QueryHelpers.AddQueryString("/Account/Logout", "c", company);
    }

    /// <summary>The <c>c</c> (company) parameter of a relative URL, if it carries one.</summary>
    private static string? Company(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return null;
        }

        var question = returnUrl.IndexOf('?', StringComparison.Ordinal);

        if (question < 0)
        {
            return null;
        }

        return QueryHelpers.ParseQuery(returnUrl[question..]).TryGetValue("c", out var value)
               && value.ToString() is { Length: > 0 } company
            ? company
            : null;
    }

    /// <summary>
    /// Refuses an absolute URL. Without this the <c>returnUrl</c> parameter is an open redirect:
    /// anyone can send <c>/Account/Login?returnUrl=https://evil.example</c> and have our own domain
    /// bounce the user onward after a successful sign-in.
    /// </summary>
    private static string Local(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl)
           && Uri.TryCreate(returnUrl, UriKind.Relative, out _)
           && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}

/// <summary>Scheme names, so they are spelled once.</summary>
public static class AuthenticationSchemes
{
    /// <summary>The browser sign-in: OAuth 2.0 + PKCE against the Flowbyte identity application.</summary>
    public const string Identity = "FlowbyteIdentity";
}

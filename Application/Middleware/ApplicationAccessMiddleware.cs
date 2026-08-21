using System.Security.Claims;
using Application.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.SignalR;

namespace Application.Middleware;

/// <summary>
/// Signs out and turns away any cookie-authenticated user who does not hold access to this
/// application in the shared identity database.
///
/// Deliberately middleware rather than an authorization policy:
///   * A policy can only fail, producing a challenge -- the cookie survives, so the user stays
///     signed in. The requirement here is that they are signed OUT.
///   * AuthorizationOptions.FallbackPolicy only applies to endpoints with no authorization
///     metadata, so every [Authorize] page -- i.e. everything worth gating -- would skip it, and
///     augmenting DefaultPolicy still misses [Authorize(Policy = "...")].
///   * backend shares AddFlowbytePolicies() between the server and the WASM client, and a
///     requirement that needs a database cannot run in the browser.
///
/// Runs after the framework-inserted authentication middleware, so HttpContext.User is settled.
/// </summary>
public sealed class ApplicationAccessMiddleware
{
    /// <summary>
    /// Paths the gate must never touch. /Account above all: the denial page lives there, and
    /// gating it would redirect-loop.
    /// </summary>
    private static readonly string[] SkipPrefixes =
    {
        "/Account",
        "/_framework",
        "/_content",
        "/_blazor",
        "/css",
        "/js",
        "/lib",
        "/images",
        "/fonts",
        "/health",
        "/healthz",
        "/hangfire"
    };

    private const string StaticAssetMetadataType = "Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor";

    private readonly RequestDelegate _next;
    private readonly ILogger<ApplicationAccessMiddleware> _logger;

    public ApplicationAccessMiddleware(RequestDelegate next, ILogger<ApplicationAccessMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IApplicationAccessService accessService)
    {
        if (await ShouldSkipAsync(context))
        {
            await _next(context);
            return;
        }

        // Ask the cookie handler directly rather than trusting HttpContext.User: on endpoints
        // marked [Authorize(AuthenticationSchemes = "ApiKey")] the principal has been replaced by
        // an API-key identity whose NameIdentifier is a key id, not a user id. Machine callers
        // have no application_user row and must not be gated. The handler memoises the result for
        // the request, so this is not a second cookie decrypt.
        var authenticated = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!authenticated.Succeeded || authenticated.Principal is null)
        {
            await _next(context);
            return;
        }

        // Post-login this claim holds application_user.id, which is what application_user_access
        // is keyed by. (The Entra object id only ever appears on the transient external principal
        // during the OIDC callback, which never reaches this middleware.)
        var userId = authenticated.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            await _next(context);
            return;
        }

        var decision = await accessService.EvaluateAsync(userId, context.RequestAborted);
        if (decision != ApplicationAccessDecision.Denied)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "Session terminated for {UserId} on {Path}: no live access to {ApplicationId}.",
            userId, context.Request.Path, accessService.ApplicationId);

        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        await context.SignOutAsync(IdentityConstants.ExternalScheme);

        if (IsProgrammaticCaller(context))
        {
            // A fetch() from the WASM client gets a status code it can react to; sending it an
            // HTML redirect would just be parsed as garbage.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers["X-Application-Access"] = "denied";
            return;
        }

        context.Response.Redirect($"{context.Request.PathBase}/Account/AccessDenied?reason=noapp");
    }

    private static async Task<bool> ShouldSkipAsync(HttpContext context)
    {
        // CORS preflight carries no cookies and must be answered.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            return true;
        }

        var path = context.Request.Path;
        foreach (var prefix in SkipPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var endpoint = context.GetEndpoint();
        if (endpoint is not null)
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                return true;
            }

            // Any SignalR hub -- /_blazor, /chathub, /realtime/salesdata.
            if (endpoint.Metadata.GetMetadata<HubMetadata>() is not null)
            {
                return true;
            }

            // Assets served by MapStaticAssets() are endpoint-routed, so UseStaticFiles does not
            // short-circuit them. Matched by type name so this file compiles in the apps that do
            // not call MapStaticAssets.
            foreach (var metadata in endpoint.Metadata)
            {
                if (metadata?.GetType().FullName == StaticAssetMetadataType)
                {
                    return true;
                }
            }
        }

        return await Task.FromResult(false);
    }

    private static bool IsProgrammaticCaller(HttpContext context)
        => context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not null
           || context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}

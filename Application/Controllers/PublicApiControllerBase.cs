using Application.Authorization;
using Application.Shared.Models.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Base for the external, API-key-authenticated public API consumed by the chat app. The API key
/// (validated by the <c>ApiKey</c> scheme, header <c>X-Api-Key</c> or <c>Authorization: Bearer</c>)
/// carries the tenant (<see cref="ApiKey.CompanyId"/>); the acting user is supplied per request via the
/// <c>X-User-Id</c> header. Access is then evaluated for that (company, user) pair.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationDefaults.Scheme)]
public abstract class PublicApiControllerBase : ControllerBase
{
    protected const string UserHeaderName = "X-User-Id";

    /// <summary>The API key resolved by the authentication handler for this request.</summary>
    protected ApiKey? CurrentKey =>
        HttpContext.Items.TryGetValue(ApiKeyAuthenticationDefaults.ApiKeyItem, out var v) ? v as ApiKey : null;

    /// <summary>Resolves (companyId from the key, userId from X-User-Id). Returns false + an error result
    /// when the key is invalid or the header is missing.</summary>
    protected bool TryGetContext(out string companyId, out string userId, out ActionResult? error)
    {
        companyId = CurrentKey?.CompanyId ?? string.Empty;
        userId = Request.Headers[UserHeaderName].FirstOrDefault() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(companyId))
        {
            error = Unauthorized("Invalid or missing API key.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(userId))
        {
            error = BadRequest($"Missing or empty '{UserHeaderName}' header.");
            return false;
        }
        error = null;
        return true;
    }
}

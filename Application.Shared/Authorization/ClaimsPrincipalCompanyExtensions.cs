using System.Security.Claims;

namespace Application.Shared.Authorization;

/// <summary>
/// Company-scoped role checks. Role claims are company-prefixed (e.g. <c>ACME_VIEW_DATA</c>), and
/// the company <c>ADMIN</c> role is implicitly allowed by every check — so an <c>{company}_ADMIN</c>
/// user can do anything for that company without holding the granular roles.
/// </summary>
public static class ClaimsPrincipalCompanyExtensions
{
    /// <summary>
    /// True when the user holds <c>{companyId}_ADMIN</c>, or any of the given company-prefixed role
    /// suffixes (e.g. <c>"VIEW_DATA"</c> → <c>{companyId}_VIEW_DATA</c>).
    /// </summary>
    public static bool HasCompanyRole(this ClaimsPrincipal user, string? companyId, params string[] suffixes)
    {
        if (user is null || string.IsNullOrWhiteSpace(companyId))
            return false;

        if (user.IsInRole($"{companyId}_ADMIN"))
            return true;

        foreach (var suffix in suffixes)
        {
            if (!string.IsNullOrEmpty(suffix) && user.IsInRole($"{companyId}_{suffix}"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the user holds <c>{companyId}_ADMIN</c>, or <b>all</b> of the given company-prefixed
    /// role suffixes. Use this for functions that require several module roles at once (e.g. a
    /// dashboard gated by both <c>DASHBOARDS_READ</c> and <c>SALES</c>). ADMIN is always allowed.
    /// </summary>
    public static bool HasAllCompanyRoles(this ClaimsPrincipal user, string? companyId, params string[] suffixes)
    {
        if (user is null || string.IsNullOrWhiteSpace(companyId))
            return false;

        if (user.IsInRole($"{companyId}_ADMIN"))
            return true;

        foreach (var suffix in suffixes)
        {
            if (string.IsNullOrEmpty(suffix) || !user.IsInRole($"{companyId}_{suffix}"))
                return false;
        }

        return suffixes.Length > 0;
    }
}

using Application.Shared.Models;

namespace Application.Shared.Services;

/// <summary>What the access gate decided about one user and one application.</summary>
public enum ApplicationAccessDecision
{
    /// <summary>The user holds a live grant. Let them through.</summary>
    Allowed,

    /// <summary>The application is live but this user has no grant, or it has been retired.</summary>
    Denied,

    /// <summary>
    /// The gate could not make a decision -- no ApplicationId configured, no matching row in
    /// dbo.application, or the database was unreachable. Callers must fail OPEN. A config typo or
    /// an unseeded environment must not lock every user out of an app with no way back in.
    /// </summary>
    Indeterminate
}

/// <summary>
/// Reads the cross-app access registry (dbo.application / dbo.application_user_access) in the
/// shared `identity` database. Backs both the header app launcher and the login gate.
/// </summary>
public interface IApplicationAccessService
{
    /// <summary>This app's id in dbo.application, from the "ApplicationId" config key.</summary>
    string? ApplicationId { get; }

    /// <summary>Apps this user may open, ordered for the launcher.</summary>
    Task<List<AppTile>> GetAppTilesForUserAsync(string applicationUserId);

    /// <summary>
    /// Evaluate access to this app.
    /// </summary>
    /// <param name="applicationUserId">
    /// MUST be application_user.id -- the ClaimTypes.NameIdentifier value on the
    /// Identity.Application cookie principal, or ApplicationUser.Id. NEVER the Entra object id:
    /// that lives in user_login.provider_key and matches nothing in application_user_access.
    /// </param>
    Task<ApplicationAccessDecision> EvaluateAsync(string applicationUserId, CancellationToken cancellationToken = default);

    /// <summary>Convenience for the login pages: anything but an outright Denied lets the user in.</summary>
    Task<bool> HasAccessAsync(string applicationUserId, CancellationToken cancellationToken = default);

    /// <summary>Display name from dbo.application.name, for the denial page. Falls back to the id.</summary>
    Task<string> GetApplicationNameAsync(CancellationToken cancellationToken = default);

    /// <summary>Drop this user's cached decision (used by the "check again" action).</summary>
    void Invalidate(string applicationUserId);
}

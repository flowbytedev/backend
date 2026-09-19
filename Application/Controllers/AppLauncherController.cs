using System.Security.Claims;
using Application.Models;
using Application.Shared.Models;
using Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Backs the header app launcher. Its own controller rather than an action on an existing one so
/// that the route is identical in every app and the Blazor component can be shared verbatim.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AppLauncherController : ControllerBase
{
    private readonly IAppLauncherClient _launcher;

    public AppLauncherController(IAppLauncherClient launcher) => _launcher = launcher;

    /// <summary>
    /// Applications the signed-in user has been granted. The user id is read from the auth cookie
    /// and never accepted from the caller, so this cannot be used to enumerate anyone else.
    /// </summary>
    [HttpGet("apps")]
    public async Task<ActionResult<Response<IReadOnlyList<AppTile>>>> Apps(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var tiles = await _launcher.GetTilesAsync(userId, cancellationToken);

        return Ok(new Response<IReadOnlyList<AppTile>>
        {
            Items = tiles,
            Status = ResponseStatus.Success
        });
    }
}

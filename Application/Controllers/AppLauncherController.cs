using System.Security.Claims;
using Application.Models;
using Application.Shared.Models;
using Application.Shared.Services;
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
    private readonly IApplicationAccessService _service;

    public AppLauncherController(IApplicationAccessService service) => _service = service;

    /// <summary>
    /// Applications the signed-in user has been granted. The user id is read from the auth cookie
    /// and never accepted from the caller, so this cannot be used to enumerate anyone else.
    /// </summary>
    [HttpGet("apps")]
    public async Task<ActionResult<Response<List<AppTile>>>> Apps()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var tiles = await _service.GetAppTilesForUserAsync(userId);

        return Ok(new Response<List<AppTile>>
        {
            Items = tiles,
            Status = ResponseStatus.Success
        });
    }
}

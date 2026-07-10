using Application.Shared.Models.WhatsNew;
using Application.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class WhatsNewController : ControllerBase
{
    private readonly IWhatsNewService _whatsNewService;

    public WhatsNewController(IWhatsNewService whatsNewService)
    {
        _whatsNewService = whatsNewService;
    }

    // GET: api/whatsnew — the feed (list + unseen count) for the current user.
    [HttpGet]
    public async Task<ActionResult<WhatsNewFeedDto>> GetFeed(CancellationToken ct)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        return Ok(await _whatsNewService.GetFeedAsync(userId, ct: ct));
    }

    // POST: api/whatsnew/seen — marks everything up to now as seen for the current user.
    [HttpPost("seen")]
    public async Task<IActionResult> MarkSeen(CancellationToken ct)
    {
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("User ID is required in headers");

        await _whatsNewService.MarkSeenAsync(userId, ct);
        return NoContent();
    }
}

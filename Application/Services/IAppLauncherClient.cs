using Application.Shared.Models;

namespace Application.Services;

/// <summary>
/// Supplies the tiles shown in the header app launcher.
/// </summary>
/// <remarks>
/// This exists as an interface because the source of those tiles moved. The inherited template read
/// them straight out of <c>dbo.application</c> — a table shared by identity, chat, backend and
/// relay. This application no longer keeps its own copy, so the tiles come from identity over HTTP.
/// <para>
/// Keeping it behind a seam matters for a second reason: an app launcher is decoration. If identity
/// is slow or down, the header should render without tiles, not fail the page. Implementations are
/// expected to degrade rather than throw.
/// </para>
/// </remarks>
public interface IAppLauncherClient
{
    /// <summary>
    /// Tiles the given user has been granted, or an empty list if they cannot be determined.
    /// </summary>
    /// <param name="userId">
    /// Identity's <c>sub</c> — the same value every Flowbyte app stores as
    /// <c>ClaimTypes.NameIdentifier</c>, and the id identity keys access on.
    /// </param>
    Task<IReadOnlyList<AppTile>> GetTilesAsync(string userId, CancellationToken cancellationToken = default);
}

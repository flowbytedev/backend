namespace Application.Shared.Models;

/// <summary>
/// One tile in the app launcher. Deliberately flat and small: this crosses the wire to the
/// WASM client on every page load, so it carries no entity graph and no audit columns.
/// </summary>
public class AppTile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public string? Description { get; set; }
}

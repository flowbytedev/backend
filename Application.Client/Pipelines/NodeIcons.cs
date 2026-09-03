using Application.Shared.Models.Data.Pipelines;
using Microsoft.AspNetCore.Components;

namespace Application.Client.Pipelines;

/// <summary>
/// Maps a catalogue icon key to an inline 24x24 stroke SVG. Literal path data rather than an icon font,
/// matching how the nav menu already draws its icons, so the canvas needs no new asset and no network call.
/// </summary>
public static class NodeIcons
{
    /// <summary>The inner path markup for an icon key, ready to drop inside an &lt;svg&gt;.</summary>
    public static MarkupString Path(string? key) => new(key switch
    {
        "database" => "<ellipse cx='12' cy='5' rx='8' ry='3'/><path d='M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5'/><path d='M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3'/>",
        "server" => "<rect x='3' y='4' width='18' height='7' rx='2'/><rect x='3' y='13' width='18' height='7' rx='2'/><path d='M7 8h.01M7 17h.01'/>",
        "file" => "<path d='M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z'/><path d='M14 3v5h5'/>",
        "columns" => "<rect x='3' y='4' width='7' height='16' rx='1'/><rect x='14' y='4' width='7' height='16' rx='1'/>",
        "filter" => "<path d='M3 5h18l-7 8v6l-4-2v-4z'/>",
        "calculator" => "<rect x='4' y='3' width='16' height='18' rx='2'/><path d='M8 7h8'/><path d='M8 12h.01M12 12h.01M16 12h.01M8 16h.01M12 16h.01M16 16h.01'/>",
        "git-merge" => "<circle cx='6' cy='5' r='2'/><circle cx='6' cy='19' r='2'/><circle cx='18' cy='12' r='2'/><path d='M6 7v10'/><path d='M8 5h4a4 4 0 0 1 4 4v1'/>",
        "layers" => "<path d='M12 3l9 5-9 5-9-5z'/><path d='M3 13l9 5 9-5'/>",
        "copy-minus" => "<rect x='8' y='8' width='12' height='12' rx='2'/><path d='M16 4H6a2 2 0 0 0-2 2v10'/><path d='M11 14h6'/>",
        "sigma" => "<path d='M18 4H6l6 8-6 8h12'/>",
        "code" => "<path d='M9 18l-6-6 6-6'/><path d='M15 6l6 6-6 6'/>",
        "download" => "<path d='M12 3v12'/><path d='M7 10l5 5 5-5'/><path d='M4 20h16'/>",
        "cloud" => "<path d='M17.5 19a4.5 4.5 0 0 0 .5-8.97A6 6 0 0 0 6.3 9.5A3.5 3.5 0 0 0 7 19z'/>",
        "upload-cloud" => "<path d='M17 17a4.5 4.5 0 0 0 .5-8.97A6 6 0 0 0 5.8 7.5A3.5 3.5 0 0 0 6.5 17'/><path d='M12 21v-8'/><path d='M9 16l3-3 3 3'/>",
        "arrow-down-up" => "<path d='M7 3v18'/><path d='M3 7l4-4 4 4'/><path d='M17 21V3'/><path d='M13 17l4 4 4-4'/>",
        "medal" => "<circle cx='12' cy='15' r='6'/><path d='M9 9L6 3h12l-3 6'/>",
        "hash" => "<path d='M4 9h16M4 15h16M10 3L8 21M16 3l-2 18'/>",
        "trending-up" => "<polyline points='3 17 9 11 13 15 21 7'/><polyline points='15 7 21 7 21 13'/>",
        "arrow-down-to-line" => "<path d='M12 3v12'/><path d='M7 10l5 5 5-5'/><path d='M5 21h14'/>",
        "type" => "<path d='M4 6h16'/><path d='M12 6v14'/><path d='M9 20h6'/>",
        "split" => "<path d='M4 4h6l4 8 4 8h-2'/><path d='M4 20h6'/><path d='M14 4h6'/>",
        "columns-3" => "<rect x='3' y='4' width='5' height='16' rx='1'/><rect x='9.5' y='4' width='5' height='16' rx='1'/><rect x='16' y='4' width='5' height='16' rx='1'/>",
        "rows-3" => "<rect x='4' y='3' width='16' height='5' rx='1'/><rect x='4' y='9.5' width='16' height='5' rx='1'/><rect x='4' y='16' width='16' height='5' rx='1'/>",
        "list" => "<path d='M8 6h13M8 12h13M8 18h13'/><path d='M3 6h.01M3 12h.01M3 18h.01'/>",
        "braces" => "<path d='M8 3H7a2 2 0 0 0-2 2v4a2 2 0 0 1-2 2 2 2 0 0 1 2 2v4a2 2 0 0 0 2 2h1'/><path d='M16 3h1a2 2 0 0 1 2 2v4a2 2 0 0 0 2 2 2 2 0 0 0-2 2v4a2 2 0 0 1-2 2h-1'/>",
        "git-fork" => "<circle cx='12' cy='18' r='3'/><circle cx='6' cy='6' r='3'/><circle cx='18' cy='6' r='3'/><path d='M18 9v1a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V9'/><path d='M12 12v3'/>",
        "mail" => "<rect x='2' y='4' width='20' height='16' rx='2'/><path d='M2 7l10 7 10-7'/>",
        "variable" => "<path d='M8 3a12 12 0 0 0 0 18'/><path d='M16 3a12 12 0 0 1 0 18'/><path d='M9 9l6 6'/><path d='M15 9l-6 6'/>",
        _ => "<circle cx='12' cy='12' r='8'/>"
    });

    /// <summary>
    /// Palette category to the CSS modifier that sets a step's accent colour. Grouping by category rather
    /// than by individual type keeps the canvas visually coherent as step types are added.
    /// </summary>
    public static string ModifierFor(string? category) => category switch
    {
        PipelineNodeCategories.Sources => "pl-node--sources",
        PipelineNodeCategories.Shape => "pl-node--shape",
        // Reshape borrows Shape's colour: both are column-level work, and a seventh accent would
        // make the canvas a colour-matching exercise rather than a readable graph.
        PipelineNodeCategories.Reshape => "pl-node--shape",
        PipelineNodeCategories.Combine => "pl-node--combine",
        PipelineNodeCategories.Summarize => "pl-node--summarize",
        PipelineNodeCategories.Sql => "pl-node--sql",
        PipelineNodeCategories.Destination => "pl-node--destination",
        _ => string.Empty
    };

    /// <summary>Run status to the step's state class and a glyph for the compact and outline views.</summary>
    public static (string Css, string Glyph) StateOf(string? status) => status switch
    {
        PipelineStepStatus.Success => ("is-ok", "✓"),
        PipelineStepStatus.Failed => ("is-failed", "✕"),
        PipelineStepStatus.Running => ("is-running", "●"),
        PipelineStepStatus.Skipped => ("is-skipped", "⊘"),
        PipelineStepStatus.Pending => ("is-pending", "·"),
        _ => (string.Empty, string.Empty)
    };

    /// <summary>Human duration. Milliseconds under a second, then seconds, then minutes.</summary>
    public static string Duration(int ms) => ms switch
    {
        < 1000 => $"{ms}ms",
        < 60_000 => $"{ms / 1000.0:0.#}s",
        _ => $"{ms / 60000}m {(ms % 60000) / 1000}s"
    };

    /// <summary>Row counts, abbreviated so a node's metric row never wraps.</summary>
    public static string Rows(long rows) => $"{Abbreviate(rows)} rows";

    /// <summary>
    /// A running count against a known total — <c>4.5k / 12k rows</c>.
    /// <para>
    /// Both sides go through the same abbreviation, and the unit is said once at the end. Formatting the
    /// two differently ("4.5k rows / 12,000") invites the reader to compare numbers written on different
    /// scales, which is the one thing a fraction exists to make easy.
    /// </para>
    /// </summary>
    public static string RowsOf(long rows, long total) =>
        $"{Abbreviate(rows)} / {Abbreviate(total)} rows";

    private static string Abbreviate(long rows) => rows switch
    {
        < 1000 => rows.ToString(),
        < 1_000_000 => $"{rows / 1000.0:0.#}k",
        _ => $"{rows / 1_000_000.0:0.##}M"
    };
}

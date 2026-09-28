using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Application.Shared.Enums;
using Application.Shared.Models;
using Microsoft.AspNetCore.Components;

namespace Application.Client.Components.Status;

/// <summary>
/// Presentation helpers shared by every status page: the state → `s-*` class mapping that status.css colours,
/// labels, the icon set, and time formatting. One mapping here replaces the per-page switch expressions the
/// old pages each carried.
/// </summary>
public static class StatusUi
{
    // ---- entity status ----------------------------------------------------------------------------------

    public static string Cls(AssetStatus s) => s switch
    {
        AssetStatus.Online => "s-online",
        AssetStatus.Offline => "s-offline",
        AssetStatus.Error => "s-error",
        AssetStatus.Degraded => "s-degraded",
        AssetStatus.Maintenance => "s-maintenance",
        _ => "s-unknown"
    };

    public static string Cls(AssetStatus? s) => s.HasValue ? Cls(s.Value) : "s-none";

    public static string Label(AssetStatus s) => s switch
    {
        AssetStatus.Online => "Online",
        AssetStatus.Offline => "Offline",
        AssetStatus.Error => "Error",
        AssetStatus.Degraded => "Degraded",
        AssetStatus.Maintenance => "Maintenance",
        _ => "Unknown"
    };

    public static bool IsDown(AssetStatus s) => s is AssetStatus.Offline or AssetStatus.Error;
    public static bool IsUnhealthy(AssetStatus s) => s is AssetStatus.Offline or AssetStatus.Error or AssetStatus.Degraded;
    public static bool NeedsAttention(AssetStatus s) => s is not (AssetStatus.Online or AssetStatus.Unknown);

    /// <summary>Higher is worse. Used to pick the worst status of a group or a day.</summary>
    public static int Rank(AssetStatus? s) => s switch
    {
        AssetStatus.Offline or AssetStatus.Error => 5,
        AssetStatus.Degraded => 4,
        AssetStatus.Maintenance => 3,
        AssetStatus.Unknown => 2,
        AssetStatus.Online => 1,
        _ => 0
    };

    /// <summary>Latest known status of an entity loaded with its status history, or Unknown.</summary>
    public static AssetStatus Current(MonitoredAsset e) =>
        e.StatusHistory?.OrderByDescending(h => h.CheckedAt).FirstOrDefault()?.Status ?? AssetStatus.Unknown;

    public static AssetStatusHistory? Latest(MonitoredAsset e) =>
        e.StatusHistory?.OrderByDescending(h => h.CheckedAt).FirstOrDefault();

    /// <summary>Per-day worst status across several entities — the strip on a board group header.</summary>
    public static List<StatusOverviewDay> WorstDays(IEnumerable<StatusOverviewEntity> entities)
    {
        var list = entities.ToList();
        if (list.Count == 0) return new();
        var axis = list.OrderByDescending(e => e.Days.Count).First().Days;
        return axis.Select((d, i) => new StatusOverviewDay
        {
            Date = d.Date,
            Status = list.Select(e => i < e.Days.Count ? e.Days[i].Status : null)
                         .OrderByDescending(Rank).FirstOrDefault()
        }).ToList();
    }

    /// <summary>The last <paramref name="n"/> days of a strip.</summary>
    public static List<StatusOverviewDay> LastDays(IEnumerable<StatusOverviewDay>? days, int n) =>
        (days ?? Enumerable.Empty<StatusOverviewDay>()).TakeLast(n).ToList();

    public static bool HasData(StatusOverviewEntity e) => e.Days.Any(d => d.Status.HasValue);

    public static string UptimeCls(double pct) => pct < 99 ? "is-bad" : pct < 99.9 ? "is-warn" : "";

    // ---- incidents ---------------------------------------------------------------------------------------

    public static string Cls(IncidentStatus s) => s switch
    {
        IncidentStatus.Open => "s-open",
        IncidentStatus.Investigating => "s-investigating",
        IncidentStatus.Identified => "s-identified",
        IncidentStatus.Monitoring => "s-monitoring",
        IncidentStatus.Resolved => "s-resolved",
        _ => "s-unknown"
    };

    public static string Cls(IncidentSeverity s) => s switch
    {
        IncidentSeverity.Low => "s-low",
        IncidentSeverity.Medium => "s-medium",
        IncidentSeverity.High => "s-high",
        IncidentSeverity.Critical => "s-critical",
        _ => "s-unknown"
    };

    public static string RowCls(Incident i) => i.Status == IncidentStatus.Resolved
        ? "st-row-muted"
        : i.Severity switch
        {
            IncidentSeverity.Critical => "st-row-critical",
            IncidentSeverity.High => "st-row-high",
            IncidentSeverity.Medium => "st-row-medium",
            _ => "st-row-low"
        };

    public static readonly IncidentStatus[] Flow =
    {
        IncidentStatus.Open, IncidentStatus.Investigating, IncidentStatus.Identified,
        IncidentStatus.Monitoring, IncidentStatus.Resolved
    };

    // ---- entity types ------------------------------------------------------------------------------------

    public static string TypeLabel(AssetType t)
    {
        var member = typeof(AssetType).GetMember(t.ToString()).FirstOrDefault();
        var name = member?.GetCustomAttribute<DisplayAttribute>()?.Name ?? t.ToString();
        // "Data Pipeline" → "Data pipeline": sentence case, matching the rest of the UI.
        return name.Length > 1 ? name[0] + name[1..].ToLowerInvariant() : name;
    }

    public static string TypePlural(AssetType t) => t switch
    {
        AssetType.Server => "Servers",
        AssetType.Database => "Databases",
        AssetType.Report => "Reports",
        AssetType.Dataset => "Datasets",
        AssetType.DataPipeline => "Data pipelines",
        AssetType.Table => "Tables",
        AssetType.DataJob => "Data jobs",
        _ => t.ToString()
    };

    public static string TypeIcon(AssetType t) => t switch
    {
        AssetType.Server => "server",
        AssetType.Database => "database",
        AssetType.Report => "report",
        AssetType.Dataset => "dataset",
        AssetType.DataPipeline => "pipeline",
        AssetType.Table => "table",
        AssetType.DataJob => "job",
        _ => "folder"
    };

    public static MarkupString TypeSvg(AssetType t, int size = 16) => Icon(TypeIcon(t), size);

    // ---- icons (lucide paths, stroke = currentColor) -----------------------------------------------------

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["server"] = "<rect x=\"2\" y=\"2\" width=\"20\" height=\"8\" rx=\"2\"/><rect x=\"2\" y=\"14\" width=\"20\" height=\"8\" rx=\"2\"/><line x1=\"6\" y1=\"6\" x2=\"6.01\" y2=\"6\"/><line x1=\"6\" y1=\"18\" x2=\"6.01\" y2=\"18\"/>",
        ["database"] = "<ellipse cx=\"12\" cy=\"5\" rx=\"9\" ry=\"3\"/><path d=\"M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5\"/><path d=\"M21 12c0 1.66-4 3-9 3s-9-1.34-9-3\"/>",
        ["report"] = "<line x1=\"18\" y1=\"20\" x2=\"18\" y2=\"10\"/><line x1=\"12\" y1=\"20\" x2=\"12\" y2=\"4\"/><line x1=\"6\" y1=\"20\" x2=\"6\" y2=\"14\"/>",
        ["dataset"] = "<polyline points=\"23 6 13.5 15.5 8.5 10.5 1 18\"/><polyline points=\"17 6 23 6 23 12\"/>",
        ["pipeline"] = "<polyline points=\"23 4 23 10 17 10\"/><polyline points=\"1 20 1 14 7 14\"/><path d=\"M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15\"/>",
        ["table"] = "<rect x=\"3\" y=\"3\" width=\"18\" height=\"18\" rx=\"2\"/><line x1=\"3\" y1=\"9\" x2=\"21\" y2=\"9\"/><line x1=\"3\" y1=\"15\" x2=\"21\" y2=\"15\"/><line x1=\"9\" y1=\"3\" x2=\"9\" y2=\"21\"/>",
        ["job"] = "<circle cx=\"12\" cy=\"12\" r=\"3\"/><path d=\"M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z\"/>",
        ["folder"] = "<path d=\"M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z\"/>",
        ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"8\"/><path d=\"m21 21-4.3-4.3\"/>",
        ["plus"] = "<path d=\"M5 12h14\"/><path d=\"M12 5v14\"/>",
        ["edit"] = "<path d=\"M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z\"/><path d=\"m15 5 4 4\"/>",
        ["trash"] = "<path d=\"M3 6h18\"/><path d=\"M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6\"/><path d=\"M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2\"/>",
        ["chev-down"] = "<path d=\"m6 9 6 6 6-6\"/>",
        ["chev-right"] = "<path d=\"m9 18 6-6-6-6\"/>",
        ["arrow-left"] = "<path d=\"m12 19-7-7 7-7\"/><path d=\"M19 12H5\"/>",
        ["ext"] = "<path d=\"M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6\"/><polyline points=\"15 3 21 3 21 9\"/><line x1=\"10\" x2=\"21\" y1=\"14\" y2=\"3\"/>",
        ["branch"] = "<line x1=\"6\" x2=\"6\" y1=\"3\" y2=\"15\"/><circle cx=\"18\" cy=\"6\" r=\"3\"/><circle cx=\"6\" cy=\"18\" r=\"3\"/><path d=\"M18 9a9 9 0 0 1-9 9\"/>",
        ["alert"] = "<path d=\"m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z\"/><path d=\"M12 9v4\"/><path d=\"M12 17h.01\"/>",
        ["check"] = "<path d=\"M20 6 9 17l-5-5\"/>",
        ["check-circle"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"m9 12 2 2 4-4\"/>",
        ["info"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M12 16v-4\"/><path d=\"M12 8h.01\"/>",
        ["refresh"] = "<path d=\"M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8\"/><path d=\"M21 3v5h-5\"/><path d=\"M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16\"/><path d=\"M8 16H3v5\"/>",
        ["x"] = "<path d=\"M18 6 6 18\"/><path d=\"m6 6 12 12\"/>",
        ["message"] = "<path d=\"M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z\"/>",
        ["activity"] = "<path d=\"M22 12h-4l-3 9L9 3l-3 9H2\"/>",
        ["shield"] = "<path d=\"M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z\"/>",
        ["bell"] = "<path d=\"M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9\"/><path d=\"M10.3 21a1.94 1.94 0 0 0 3.4 0\"/>",
        ["up-right"] = "<path d=\"M7 7h10v10\"/><path d=\"M7 17 17 7\"/>",
        ["down-right"] = "<path d=\"m7 7 10 10\"/><path d=\"M17 7v10H7\"/>",
        ["link"] = "<path d=\"M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71\"/><path d=\"M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71\"/>",
        ["sort"] = "<path d=\"m7 15 5 5 5-5\"/><path d=\"m7 9 5-5 5 5\"/>",
        ["sort-asc"] = "<path d=\"m7 14 5-5 5 5\"/>",
        ["sort-desc"] = "<path d=\"m7 10 5 5 5-5\"/>",
        ["grid"] = "<rect width=\"7\" height=\"7\" x=\"3\" y=\"3\" rx=\"1\"/><rect width=\"7\" height=\"7\" x=\"14\" y=\"3\" rx=\"1\"/><rect width=\"7\" height=\"7\" x=\"14\" y=\"14\" rx=\"1\"/><rect width=\"7\" height=\"7\" x=\"3\" y=\"14\" rx=\"1\"/>",
        ["lock"] = "<rect width=\"18\" height=\"11\" x=\"3\" y=\"11\" rx=\"2\" ry=\"2\"/><path d=\"M7 11V7a5 5 0 0 1 10 0v4\"/>",
        ["inbox"] = "<polyline points=\"22 12 16 12 14 15 10 15 8 12 2 12\"/><path d=\"M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z\"/>",
        ["user"] = "<path d=\"M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2\"/><circle cx=\"12\" cy=\"7\" r=\"4\"/>",
        ["clock"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><polyline points=\"12 6 12 12 16 14\"/>",
    };

    public static MarkupString Icon(string name, int size = 14) =>
        new($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">{(Paths.TryGetValue(name, out var p) ? p : "")}</svg>");

    // ---- time --------------------------------------------------------------------------------------------

    /// <summary>Stored timestamps are UTC; a value that arrives without a kind is treated as UTC too.</summary>
    public static DateTime Local(DateTime d) =>
        d.Kind == DateTimeKind.Local ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();

    public static string Duration(TimeSpan? d)
    {
        if (!d.HasValue) return "—";
        var v = d.Value < TimeSpan.Zero ? TimeSpan.Zero : d.Value;
        if (v.TotalDays >= 1) return $"{(int)v.TotalDays}d {v.Hours}h";
        if (v.TotalHours >= 1) return $"{(int)v.TotalHours}h {v.Minutes}m";
        return $"{Math.Max(0, (int)v.TotalMinutes)}m";
    }

    public static string Ago(DateTime? utc)
    {
        if (!utc.HasValue) return "never";
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        if (span.TotalSeconds < 60) return "just now";
        return Duration(span) + " ago";
    }

    /// <summary>"14:32" today, "27 Sep, 14:32" this year, "27 Sep 2025" otherwise.</summary>
    public static string When(DateTime? utc)
    {
        if (!utc.HasValue) return "—";
        var l = Local(utc.Value);
        var now = DateTime.Now;
        if (l.Date == now.Date) return l.ToString("HH:mm");
        if (l.Year == now.Year) return l.ToString("d MMM, HH:mm");
        return l.ToString("d MMM yyyy");
    }

    public static string FullWhen(DateTime? utc) => utc.HasValue ? Local(utc.Value).ToString("d MMM yyyy, HH:mm") : "—";

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var local = name.Contains('@') ? name.Split('@')[0].Replace('.', ' ') : name;
        var parts = local.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}"
        };
    }

    public static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    public static string Ms(double? v) => v.HasValue ? $"{v.Value:#,0} ms".Replace(",", " ") : "—";
}

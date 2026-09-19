using System;
using System.Collections.Generic;
using System.Linq;

namespace Application.Shared.Models;

/// <summary>
/// The date patterns a company may pick for CSV exports, shared by the settings UI (dropdown) and the API
/// (validation) so the two can't drift apart.
/// <para>
/// Deliberately a fixed allowlist rather than a free-text field: the stored value is handed straight to
/// <c>DateTime.ToString</c> on the export path, where an arbitrary pattern could throw mid-download or
/// silently emit nonsense, and a custom pattern can't be meaningfully validated.
/// </para>
/// </summary>
public static class ExportDateFormats
{
    /// <summary>
    /// Day-first. The app default: exports previously rendered whatever the server's culture produced
    /// (MM/dd/yyyy on a US-culture host), which reads as the wrong date for a day-first audience.
    /// </summary>
    public const string Default = "dd/MM/yyyy";

    /// <summary>The time part appended to values that carry one — see <see cref="ForTimestamp"/>.</summary>
    private const string TimeSuffix = " HH:mm:ss";

    /// <summary>Selectable patterns, in the order the settings dropdown lists them.</summary>
    public static readonly IReadOnlyList<string> Allowed = new[]
    {
        "dd/MM/yyyy",
        "MM/dd/yyyy",
        "yyyy-MM-dd",
        "dd-MM-yyyy",
        "dd.MM.yyyy",
    };

    public static bool IsAllowed(string? format)
        => format != null && Allowed.Contains(format, StringComparer.Ordinal);

    /// <summary>
    /// The pattern to actually format with: the stored value when it's one we recognise, otherwise the
    /// default. Keeps a stale or hand-edited database value from breaking a download.
    /// </summary>
    public static string Resolve(string? stored) => IsAllowed(stored) ? stored! : Default;

    /// <summary>
    /// The date pattern extended with a time part, for values that actually carry a time. Exporting a
    /// timestamp through a date-only pattern would silently drop the time, which is data loss in an export.
    /// </summary>
    public static string ForTimestamp(string dateFormat) => dateFormat + TimeSuffix;

    /// <summary>
    /// The DuckDB <c>strftime</c> spelling of a pattern, for the streaming export path where DuckDB — not
    /// .NET — writes the file (<c>COPY … TO … (FORMAT CSV, DATEFORMAT …)</c>).
    /// <para>
    /// A lookup over the fixed allowlist rather than a general .NET-to-strftime translator: the set of
    /// patterns is closed, and a translator would be a second place for the two spellings of one format to
    /// drift apart. An unrecognised value maps to <see cref="Default"/>'s pattern, matching
    /// <see cref="Resolve"/>.
    /// </para>
    /// <para>
    /// Note the one behavioural difference from <c>CsvExportFormatter</c>, which formats in .NET: that path
    /// picks date-only vs. timestamp <em>per value</em> (a midnight <c>DateTime</c> loses its time part),
    /// whereas DuckDB applies DATEFORMAT and TIMESTAMPFORMAT <em>per column type</em>. A TIMESTAMP column
    /// whose values are all midnight therefore exports with <c>00:00:00</c> here and without it there.
    /// </para>
    /// </summary>
    public static string ToStrftime(string? dateFormat) => Resolve(dateFormat) switch
    {
        "dd/MM/yyyy" => "%d/%m/%Y",
        "MM/dd/yyyy" => "%m/%d/%Y",
        "yyyy-MM-dd" => "%Y-%m-%d",
        "dd-MM-yyyy" => "%d-%m-%Y",
        "dd.MM.yyyy" => "%d.%m.%Y",
        _ => "%d/%m/%Y",
    };

    /// <summary>
    /// The strftime pattern for TIMESTAMP columns: <see cref="ToStrftime"/> plus the time part, mirroring
    /// what <see cref="ForTimestamp"/> does for the in-process formatter.
    /// </summary>
    public static string ToTimestampStrftime(string? dateFormat) => ToStrftime(dateFormat) + " %H:%M:%S";
}

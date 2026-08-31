namespace Application.Shared.Models;

/// <summary>
/// The one place a schedule's time zone is named and resolved — pipelines, ingestion sources, notebook runs
/// and the fixed recurring jobs in the scheduler's <c>Program.cs</c> all come through here.
/// <para>
/// <b>Why resolution is not just <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/>.</b> Hangfire
/// stores <see cref="TimeZoneInfo.Id"/> as a plain string on the recurring job and re-resolves it later, on
/// whichever server happens to schedule the next occurrence — not on the one that registered it. And .NET
/// does not normalise the id it hands back: ask a machine with ICU for "Asia/Beirut" and you get an object
/// whose <c>Id</c> is still "Asia/Beirut". Store that, and any server running without ICU (Windows before
/// 1903, or invariant globalization) throws <see cref="TimeZoneNotFoundException"/> when its scheduler
/// reaches that job — the schedule simply never fires, and the only sign is a stack trace in that one
/// server's log.
/// </para>
/// <para>
/// So an id is resolved to its <b>Windows</b> form where a conversion exists. That is the form with the
/// widest reach in this deployment: a Windows host knows it with or without ICU, and an ICU-enabled Unix
/// host converts it back. Our own tables keep the IANA id — it is portable and it is what the editor shows;
/// the conversion belongs at the Hangfire boundary, not in the data.
/// </para>
/// </summary>
public static class ScheduleTimeZones
{
    /// <summary>The zone a schedule that names none runs in.</summary>
    public const string DefaultId = "Asia/Beirut";

    /// <summary>
    /// The same zone under the id Windows uses. Named explicitly because a host without ICU cannot convert
    /// to it either, and this is the only string such a host will accept for Beirut.
    /// </summary>
    private const string DefaultWindowsId = "Middle East Standard Time";

    /// <summary>
    /// The zone used when a schedule names none. Null only on a host with no usable time zone data, where
    /// the caller has nothing better to do than let Hangfire fall back to the server's local time.
    /// </summary>
    public static TimeZoneInfo? Default => Resolve(DefaultId) ?? Lookup(DefaultWindowsId);

    /// <summary>
    /// The zone this id names, in the form safest to persist, or null when this host cannot resolve it at
    /// all. Accepts both IANA ("Asia/Beirut") and Windows ("Middle East Standard Time") ids.
    /// </summary>
    public static TimeZoneInfo? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var trimmed = id!.Trim();
        var windowsForm = ToWindowsId(trimmed);

        return (windowsForm is null ? null : Lookup(windowsForm)) ?? Lookup(trimmed);
    }

    /// <summary>Whether a saved zone will still mean something to the box that runs the schedule.</summary>
    public static bool IsKnown(string? id) => Resolve(id) is not null;

    /// <summary>
    /// The Windows id for this zone, or null when there is no conversion — either because it already is a
    /// Windows id, or because this host has no ICU data to convert with.
    /// </summary>
    private static string? ToWindowsId(string id) =>
        TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) ? windows : null;

    private static TimeZoneInfo? Lookup(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}

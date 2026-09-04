namespace Application.Shared.Models;

/// <summary>
/// Answers one question: <b>when was this cron expression last due?</b> Needed by freshness checks, where
/// "did the step run since it was supposed to" cannot be asked without knowing when that was.
/// <para>
/// <b>Why this exists rather than a package.</b> Hangfire owns cron evaluation everywhere else in this
/// solution, but it does not expose a "previous occurrence" API — and Cronos, which it vendors internally,
/// is not a package this build can add (see the restore note in <c>Application.Shared.csproj</c>). So this
/// is a deliberately small parser covering the field grammar real schedules use, and <b>refusing</b>
/// everything else rather than guessing.
/// </para>
/// <para>
/// <b>It is stricter than the schedule validator on purpose.</b> <c>PipelineService.LooksLikeCron</c> is
/// shallow because Hangfire does the real parse afterwards and will complain if it disagrees. Nothing
/// downstream of this re-checks anything, so a form it cannot evaluate has to come back as an error the
/// caller reports — an evaluator that quietly mis-reads <c>L</c> would move a deadline by up to a month and
/// call a late pipeline healthy.
/// </para>
/// </summary>
public static class CronDeadline
{
    /// <summary>
    /// How far back a deadline is looked for. Covers a once-a-year expression; beyond that the answer is
    /// null and the caller reports the policy as unevaluatable rather than inventing a deadline.
    /// </summary>
    private const int MaxDaysBack = 400;

    /// <summary>
    /// The most recent instant this expression was due, at or before <paramref name="utcNow"/>, or null
    /// when the expression cannot be read or has no occurrence in range.
    /// </summary>
    /// <param name="zone">Zone the expression's wall-clock fields are read in. Null means UTC.</param>
    public static DateTime? MostRecent(string? cron, TimeZoneInfo? zone, DateTime utcNow)
    {
        if (!CronFields.TryParse(cron, out var fields, out _)) return null;

        var tz = zone ?? TimeZoneInfo.Utc;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var today = nowLocal.Date;

        for (var back = 0; back <= MaxDaysBack; back++)
        {
            var date = today.AddDays(-back);
            if (!fields!.MatchesDate(date)) continue;

            // Only today is capped at the current minute; every earlier matching day is searched from its
            // last minute, because the most recent occurrence on that day is the one that matters.
            var from = back == 0 ? nowLocal.Hour * 60 + nowLocal.Minute : (24 * 60) - 1;

            for (var minute = from; minute >= 0; minute--)
            {
                if (!fields.MatchesTime(minute / 60, minute % 60)) continue;

                var local = DateTime.SpecifyKind(date.AddMinutes(minute), DateTimeKind.Unspecified);

                // A local time the clocks jumped over never happened, so it was never a deadline.
                if (tz.IsInvalidTime(local)) continue;

                // An ambiguous local time (the repeated hour when clocks go back) resolves to the standard
                // offset, which is the *later* of the two instants. Deliberate: a later deadline is the
                // lenient reading, and a freshness check should not manufacture staleness out of a DST edge.
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
                if (utc <= utcNow) return utc;
            }
        }

        return null;
    }
}

/// <summary>
/// A parsed cron expression as five sets of permitted values. Minute-granular: a six-field expression's
/// leading seconds field is accepted and discarded, because nothing here needs sub-minute resolution and
/// rejecting the form would refuse expressions Hangfire happily schedules.
/// </summary>
public sealed class CronFields
{
    private bool[] _minutes = Full(60);
    private bool[] _hours = Full(24);
    private bool[] _daysOfMonth = Full(32);   // 1-31, index 0 unused
    private bool[] _months = Full(13);        // 1-12, index 0 unused
    private bool[] _daysOfWeek = Full(7);     // 0-6, Sunday = 0

    /// <summary>
    /// True when both day fields are restricted. Cron's oldest quirk: in that case the two are OR'd, not
    /// AND'd, so <c>0 0 13 * FRI</c> means "the 13th, and every Friday" rather than "Friday the 13th".
    /// Getting this backwards would silently narrow a deadline to almost never.
    /// </summary>
    private bool _bothDaysRestricted;

    /// <summary>Whether this expression is one <see cref="CronDeadline"/> can evaluate.</summary>
    public static bool LooksParseable(string? cron) => TryParse(cron, out _, out _);

    public static bool TryParse(string? cron, out CronFields? fields, out string? error)
    {
        fields = null;
        error = null;

        if (string.IsNullOrWhiteSpace(cron))
        {
            error = "empty expression";
            return false;
        }

        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Six fields is Hangfire's seconds-first form. Drop the seconds rather than refuse it.
        if (parts.Length == 6) parts = parts[1..];

        if (parts.Length != 5)
        {
            error = "expected five fields";
            return false;
        }

        var result = new CronFields();

        if (!TryField(parts[0], 0, 59, null, out result._minutes, out error)) return false;
        if (!TryField(parts[1], 0, 23, null, out result._hours, out error)) return false;
        if (!TryField(parts[2], 1, 31, null, out result._daysOfMonth, out error)) return false;
        if (!TryField(parts[3], 1, 12, Months, out result._months, out error)) return false;
        if (!TryField(parts[4], 0, 7, Days, out var dow, out error)) return false;

        // Day 7 and day 0 are both Sunday. Folded here so every comparison downstream is against 0-6.
        if (dow[7]) dow[0] = true;
        result._daysOfWeek = dow[..7];

        result._bothDaysRestricted = IsRestricted(parts[2]) && IsRestricted(parts[4]);

        fields = result;
        return true;
    }

    public bool MatchesTime(int hour, int minute) => _hours[hour] && _minutes[minute];

    public bool MatchesDate(DateTime date)
    {
        if (!_months[date.Month]) return false;

        var dom = _daysOfMonth[date.Day];
        var dow = _daysOfWeek[(int)date.DayOfWeek];

        return _bothDaysRestricted ? dom || dow : dom && dow;
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>A field is unrestricted when it names every value — the only thing that turns off the OR.</summary>
    private static bool IsRestricted(string field) =>
        !string.Equals(field, "*", StringComparison.Ordinal)
        && !string.Equals(field, "?", StringComparison.Ordinal);

    private static bool TryField(
        string field, int min, int max, Dictionary<string, int>? names,
        out bool[] mask, out string? error)
    {
        mask = new bool[max + 1];
        error = null;

        foreach (var term in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = 1;
            var body = term;

            var slash = term.IndexOf('/');
            if (slash >= 0)
            {
                body = term[..slash];
                if (!int.TryParse(term[(slash + 1)..], out step) || step <= 0)
                {
                    error = $"'{term}' has an unreadable step";
                    return false;
                }
            }

            int from, to;

            if (body is "*" or "?")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = body.IndexOf('-');
                if (dash > 0)
                {
                    if (!TryValue(body[..dash], min, max, names, out from, out error)) return false;
                    if (!TryValue(body[(dash + 1)..], min, max, names, out to, out error)) return false;
                }
                else
                {
                    if (!TryValue(body, min, max, names, out from, out error)) return false;

                    // `5/10` means "from 5 onwards, every 10th" — a bare value with no step is just itself.
                    to = slash >= 0 ? max : from;
                }
            }

            if (from > to)
            {
                // Wrapping ranges (FRI-MON) are a real cron feature this does not implement. Refused
                // rather than silently read as an empty set, which would make the deadline unreachable.
                error = $"'{term}' wraps around, which is not supported";
                return false;
            }

            for (var value = from; value <= to; value += step)
                mask[value] = true;
        }

        if (Array.TrueForAll(mask, set => !set))
        {
            error = $"'{field}' matches nothing";
            return false;
        }

        return true;
    }

    private static bool TryValue(
        string text, int min, int max, Dictionary<string, int>? names, out int value, out string? error)
    {
        error = null;

        if (names is not null && names.TryGetValue(text, out value))
            return true;

        if (!int.TryParse(text, out value))
        {
            // Reached by L / W / # and by month or day names in a field that has none. All are forms a
            // freshness deadline cannot evaluate, and the policy validator reports them as such.
            error = $"'{text}' is not a value this server can read";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"'{text}' is outside {min}-{max}";
            return false;
        }

        return true;
    }

    private static bool[] Full(int length)
    {
        var mask = new bool[length];
        Array.Fill(mask, true);
        return mask;
    }

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAY"] = 5, ["JUN"] = 6,
        ["JUL"] = 7, ["AUG"] = 8, ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12
    };

    private static readonly Dictionary<string, int> Days = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUN"] = 0, ["MON"] = 1, ["TUE"] = 2, ["WED"] = 3, ["THU"] = 4, ["FRI"] = 5, ["SAT"] = 6
    };
}

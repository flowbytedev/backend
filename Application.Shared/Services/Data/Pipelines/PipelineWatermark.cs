using System.Globalization;
using System.Text.Json.Nodes;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The incremental window for one source step, and the SQL predicate that expresses it.
/// <para>
/// <b>Why the bounds are asymmetric.</b> The window is <c>&gt; low AND &lt;= high</c>, where <c>high</c> is
/// read from the source <i>before</i> the load. Scheduled ingestion instead reads the new mark from the
/// destination <i>after</i> loading and filters <c>&gt; last</c>. That is simpler and wrong in two ways:
/// rows sharing the boundary value are skipped forever, and rows written to the source while the load was
/// running get a mark that already covers them, so they are never read. Capturing the ceiling first closes
/// both — anything arriving mid-load is above the ceiling and belongs to the next run.
/// </para>
/// </summary>
public sealed class PipelineWatermarkWindow
{
    /// <summary>Last committed value, or null on the first run / after a reset.</summary>
    public string? Low { get; init; }

    /// <summary>Ceiling captured from the source for this run. Null when the source is empty.</summary>
    public string? High { get; init; }

    /// <summary>DuckDB type the ceiling came back as.</summary>
    public string? Type { get; init; }

    public string Column { get; init; } = string.Empty;

    /// <summary>
    /// True when there is nothing to read: the source's ceiling is not above the last committed low, so the
    /// step can be short-circuited rather than running a query guaranteed to return nothing.
    /// </summary>
    public bool IsEmpty => High is null || (Low is not null && string.Equals(Low, High, StringComparison.Ordinal));

    /// <summary>
    /// The predicate for this run. Three distinct outcomes, and the middle one is the whole reason this is
    /// not just "null means everything":
    /// <list type="bullet">
    /// <item><b>null</b> — the step is not incremental, read the lot.</item>
    /// <item><b><c>1 = 0</c></b> — incremental, but the source has no non-null value in the watermark
    /// column, so there is nothing to read. Returning null here would mean "full load", and since the
    /// ceiling would still be null next time, EVERY run would be a full load — appending the whole table
    /// again each night.</item>
    /// <item><b>the bounds</b> — the normal case.</item>
    /// </list>
    /// </summary>
    public string? ToPredicate()
    {
        if (string.IsNullOrWhiteSpace(Column)) return null;
        if (High is null) return "1 = 0";

        var column = Quote(Column);
        var high = $"{column} <= {Literal(High)}";

        return Low is null ? high : $"{column} > {Literal(Low)} AND {high}";
    }

    /// <summary>Human-readable, for the run log — this is what makes a short load explicable.</summary>
    public string Describe() =>
        High is null
            ? $"{Column} has no non-null values — nothing to read"
            : Low is null
                ? $"{Column} <= {High} (first run)"
                : $"{Column} > {Low} and <= {High}";

    /// <summary>
    /// Quotes a watermark value for SQL. Numbers go bare, everything else is single-quoted — the same rule
    /// <c>IngestionService.IncrementalLiteral</c> uses, kept identical on purpose so the two features cannot
    /// disagree about what <c>2026-01-01</c> means.
    /// </summary>
    public static string Literal(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return value;

        return "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Normalizes a value read back from DuckDB into the invariant text stored as the watermark.
    /// <para>
    /// Explicit formats throughout: <c>DateOnly.ToString()</c> under the invariant culture is
    /// <c>MM/dd/yyyy</c>, which would be compared as a string against an ISO value on the next run and quietly
    /// select the wrong window.
    /// </para>
    /// </summary>
    public static string? Portable(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

/// <summary>How a source step is configured for incremental reading.</summary>
public sealed class PipelineIncrementalConfig
{
    /// <summary>Column carrying an ever-increasing value. Empty means the step is not incremental.</summary>
    public string? Column { get; init; }

    /// <summary>
    /// Value to start from on the very first run. Without it the first run reads everything, which is
    /// usually right — but not for a table with ten years of history nobody wants.
    /// </summary>
    public string? Start { get; init; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Column);

    public static PipelineIncrementalConfig FromConfig(JsonObject? config, Func<string?, string?> resolve)
    {
        string? Text(string key)
        {
            var value = config?[key];
            if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            return string.IsNullOrWhiteSpace(s) ? null : resolve(s);
        }

        return new PipelineIncrementalConfig
        {
            Column = Text("incrementalColumn"),
            Start = Text("incrementalStart")
        };
    }
}

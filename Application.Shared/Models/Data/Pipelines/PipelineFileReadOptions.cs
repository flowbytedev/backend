using System.Text.Json.Nodes;

namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// How a delimited file is parsed. Every property is nullable and null means "let DuckDB decide", which is
/// exactly what the reader did before this type existed — so an existing pipeline that sets none of these
/// produces byte-identical output.
/// <para>
/// This is not configurability for its own sake. A file's delimiter and quoting are decided by whoever
/// exported it, and getting them wrong does not fail — it silently produces one wide column, or splits a
/// name in half at a comma inside quotes. No downstream transform can repair that, because the damage
/// happened before the data became rows.
/// </para>
/// </summary>
public sealed class PipelineFileReadOptions
{
    /// <summary>Field separator. <c>\t</c> is accepted and passed through as a tab.</summary>
    public string? Delimiter { get; set; }

    /// <summary>Quote character; empty string disables quoting.</summary>
    public string? Quote { get; set; }

    /// <summary>Escape character inside a quoted field.</summary>
    public string? Escape { get; set; }

    /// <summary>Text that means NULL — the <c>"NULL"</c>, <c>"\N"</c>, <c>"-"</c> sentinels exports use.</summary>
    public string? NullString { get; set; }

    /// <summary>Lines to discard before the header. For exports with a title or timestamp preamble.</summary>
    public int? SkipRows { get; set; }

    /// <summary>DuckDB codec name: <c>gzip</c>, <c>zstd</c>, <c>none</c>, or <c>auto</c>.</summary>
    public string? Compression { get; set; }

    /// <summary>DuckDB accepts a narrow set here — <c>utf-8</c>, <c>utf-16</c>, <c>latin-1</c>.</summary>
    public string? Encoding { get; set; }

    /// <summary>strptime format for DATE columns, e.g. <c>%d/%m/%Y</c>.</summary>
    public string? DateFormat { get; set; }

    /// <summary>strptime format for TIMESTAMP columns.</summary>
    public string? TimestampFormat { get; set; }

    /// <summary>Decimal point, for the European <c>1.234,56</c> convention.</summary>
    public string? DecimalSeparator { get; set; }

    /// <summary>
    /// Read every column as text, leaving conversion to a later Map step. The escape hatch for a file whose
    /// inference goes wrong — a leading-zero product code read as a number, for instance.
    /// </summary>
    public bool? AllText { get; set; }

    /// <summary>
    /// Skip lines that cannot be parsed instead of failing. Verified behaviour on DuckDB 1.3: the whole
    /// LINE is dropped, not the offending value nulled.
    /// </summary>
    public bool? IgnoreErrors { get; set; }

    /// <summary>
    /// Record the skipped lines in DuckDB's <c>reject_errors</c> table so they can be quarantined.
    /// <para>
    /// That table is scoped to the CONNECTION, so whoever sets this must read the rejects before the
    /// connection closes. Since every pipeline operation opens its own connection, that means inside the
    /// same call — there is no reading them afterwards.
    /// </para>
    /// </summary>
    public bool? StoreRejects { get; set; }

    /// <summary>True when nothing is set, so the caller can skip the whole code path.</summary>
    public bool IsEmpty =>
        Delimiter is null && Quote is null && Escape is null && NullString is null
        && SkipRows is null && Compression is null && Encoding is null
        && DateFormat is null && TimestampFormat is null && DecimalSeparator is null
        && AllText is null && IgnoreErrors is null && StoreRejects is null;

    /// <summary>
    /// Reads the options a <c>source.file</c> node carries. Tokens are resolved by the caller, because only
    /// it knows the run context.
    /// </summary>
    public static PipelineFileReadOptions FromConfig(JsonObject? config, Func<string?, string?> resolve)
    {
        if (config is null) return new PipelineFileReadOptions();

        string? Text(string key)
        {
            var value = config[key];
            if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            // Deliberately NOT IsNullOrWhiteSpace: a single space is a legitimate delimiter, and an empty
            // string is how quoting gets turned off.
            return s.Length == 0 ? s : resolve(s);
        }

        int? Number(string key)
        {
            var value = config[key];
            if (value is not JsonValue v) return null;
            if (v.TryGetValue<int>(out var i)) return i;
            return int.TryParse(v.ToString(), out var parsed) ? parsed : null;
        }

        bool? Flag(string key)
        {
            var value = config[key];
            if (value is not JsonValue v) return null;
            if (v.TryGetValue<bool>(out var b)) return b;
            return bool.TryParse(v.ToString(), out var parsed) ? parsed : null;
        }

        return new PipelineFileReadOptions
        {
            Delimiter = Text("delimiter"),
            Quote = Text("quote"),
            Escape = Text("escape"),
            NullString = Text("nullString"),
            SkipRows = Number("skipRows"),
            Compression = Text("compression"),
            Encoding = Text("encoding"),
            DateFormat = Text("dateFormat"),
            TimestampFormat = Text("timestampFormat"),
            DecimalSeparator = Text("decimalSeparator"),
            AllText = Flag("allText"),
            IgnoreErrors = Flag("ignoreErrors"),
            StoreRejects = Flag("storeRejects")
        };
    }
}

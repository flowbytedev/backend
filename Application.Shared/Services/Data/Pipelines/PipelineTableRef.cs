using Application.Shared.Enums;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// A table name a pipeline author typed, split into its schema and table parts.
/// <para>
/// Exists because <c>product.item</c> is how people write a qualified table, and every place that took the
/// value straight from the box quoted it whole — producing the single identifier <c>"product.item"</c>,
/// which no engine resolves to the table that was meant. The destination step has no schema field of its
/// own, so a dotted name is the only way to say it there at all.
/// </para>
/// <para>
/// Deliberately not a general SQL parser. It handles <c>table</c>, <c>schema.table</c>, and either part
/// quoted; anything more layered is refused with a message rather than guessed at, because a wrong guess
/// here writes rows into a table nobody is looking at.
/// </para>
/// </summary>
public sealed record PipelineTableRef(string? Schema, string Table, string? Error)
{
    public bool IsQualified => !string.IsNullOrWhiteSpace(Schema);

    /// <summary>
    /// Splits what the author typed.
    /// <para>
    /// A dotted name wins over <paramref name="schemaField"/>: it is the more specific statement, and it is
    /// what the person was looking at when they typed it. Wrapping the whole name in quotes opts out of
    /// splitting, which is how a table whose name genuinely contains a dot stays reachable.
    /// </para>
    /// </summary>
    public static PipelineTableRef Parse(string? qualified, string? schemaField = null)
    {
        var text = (qualified ?? string.Empty).Trim();

        if (text.Length == 0)
            return new PipelineTableRef(null, string.Empty, "This step has no table.");

        // Split on dots that are OUTSIDE quotes. Testing whether the whole string is quoted does not work:
        // "product"."item" both starts and ends with a quote yet is two identifiers, while "my.table" is
        // one. Only tracking quote state can tell them apart.
        var parts = SplitOutsideQuotes(text, out var unterminated);

        if (unterminated)
            return new PipelineTableRef(null, string.Empty,
                $"'{text}' has an unclosed quote.");

        if (parts.Any(part => part.Trim().Length == 0))
            return new PipelineTableRef(null, string.Empty,
                $"'{text}' has an empty part — check the dots.");

        // A three-part name (database.schema.table) is a different thing from what the connection already
        // pins down, so it is refused rather than silently reinterpreted.
        if (parts.Count > 2)
            return new PipelineTableRef(null, string.Empty,
                $"'{text}' has too many parts. Use schema.table — the database comes from the connection.");

        return parts.Count == 1
            ? new PipelineTableRef(Clean(schemaField), Unwrap(parts[0]), null)
            : new PipelineTableRef(Unwrap(parts[0]), Unwrap(parts[1]), null);
    }

    /// <summary>Renders for a destination engine, using that engine's own quoting and schema rules.</summary>
    public string ForEngine(DataSourceType engine) =>
        SqlTypeMapper.QualifiedTable(engine, Schema, Table);

    /// <summary>Renders for DuckDB, which quotes with double quotes and does have schemas.</summary>
    public string ForDuckDb() =>
        IsQualified ? $"{PipelineSql.Q(Schema!)}.{PipelineSql.Q(Table)}" : PipelineSql.Q(Table);

    /// <summary>What to show a person — unquoted, and only dotted when it really is.</summary>
    public string Display() => IsQualified ? $"{Schema}.{Table}" : Table;

    /// <summary>
    /// Splits on dots at quote depth zero, so a dot inside a quoted identifier stays part of the name.
    /// <para>
    /// Handles the doubling convention (<c>""</c> inside a double-quoted name) — without it, a name
    /// containing an escaped quote would end the quote early and the rest of the name would be split on
    /// its dots.
    /// </para>
    /// </summary>
    private static List<string> SplitOutsideQuotes(string text, out bool unterminated)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();

        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote is null)
            {
                if (c is '"' or '`' or '[')
                {
                    quote = c == '[' ? ']' : c;
                    current.Append(c);
                    continue;
                }

                if (c == '.')
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
                continue;
            }

            // A doubled closing quote is a literal one, not the end of the identifier.
            if (c == quote && i + 1 < text.Length && text[i + 1] == quote && quote != ']')
            {
                current.Append(c).Append(c);
                i++;
                continue;
            }

            if (c == quote) quote = null;
            current.Append(c);
        }

        parts.Add(current.ToString());
        unterminated = quote is not null;
        return parts;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Unwrap(value.Trim());

    /// <summary>True when the whole string is one quoted identifier.</summary>
    private static bool IsWrapped(string text) =>
        text.Length >= 2
        && ((text[0] == '"' && text[^1] == '"')
            || (text[0] == '[' && text[^1] == ']')
            || (text[0] == '`' && text[^1] == '`'));

    /// <summary>
    /// Strips one layer of quoting. The parts are re-quoted per engine downstream, so carrying the author's
    /// quote style any further would produce <c>["[dbo]"]</c>.
    /// </summary>
    private static string Unwrap(string text)
    {
        var trimmed = text.Trim();
        if (!IsWrapped(trimmed)) return trimmed;

        var inner = trimmed[1..^1];

        // Undo the doubling that quoting a quote requires.
        return trimmed[0] switch
        {
            '"' => inner.Replace("\"\"", "\""),
            '`' => inner.Replace("``", "`"),
            _ => inner.Replace("]]", "]")
        };
    }
}

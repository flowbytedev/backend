using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Enums;
using Application.Shared.Models;

namespace Application.Shared.Services.Data;

/// <summary>
/// Works out which tables a piece of ad-hoc SQL refers to, and which of them the acting user is allowed
/// to read. Shared by the internal SQL workbench (<c>QueryController</c>) and the API-key-authenticated
/// public query endpoint, so there is exactly one implementation of "what does this query touch".
/// </summary>
/// <remarks>
/// The two callers apply <b>different policies</b> to the same facts, which is the reason this returns a
/// resolution object rather than a yes/no:
/// <list type="bullet">
/// <item>
/// The workbench is <b>permissive</b>: it blocks only a reference that matches a table it knows about and
/// that the user may not read (<see cref="SqlTableResolution.FirstDisallowedKnownTable"/>). Anything it
/// cannot identify — a CTE, an alias, a function — is left alone, so a legitimate query is never
/// false-flagged. That is the behaviour it has always had and it is preserved here exactly.
/// </item>
/// <item>
/// The public endpoint is <b>strict</b>: every table reference must resolve to a table the user may read
/// or to a CTE the query itself declares, and anything else is refused. It can afford this because the
/// model is told up front exactly which tables exist, and because a wrong answer there is a data leak
/// rather than an inconvenience.
/// </item>
/// </list>
/// <para>
/// Neither policy is a SQL parser and neither pretends to be. The strict policy is safe not because this
/// extraction is exhaustive but because the query is ultimately rewritten to run against secured
/// relations (see <see cref="SecuredSqlBuilder"/>) — extraction decides what to secure, it is not the
/// boundary itself.
/// </para>
/// </remarks>
public interface ISqlTableResolver
{
    Task<SqlTableResolution> ResolveAsync(Dataset dataset, string userId, string companyId,
        string sql, bool snapshotMode, CancellationToken ct = default);
}

/// <summary>One table-ish thing the SQL refers to, and where it sits in the original text.</summary>
/// <param name="Raw">The reference exactly as written, e.g. <c>main."orders"</c>.</param>
/// <param name="Cleaned">Quoting removed, parts rejoined with dots, e.g. <c>main.orders</c>.</param>
/// <param name="Name">The last part only, unquoted, e.g. <c>orders</c>.</param>
/// <param name="IsQualified">True when the reference carries a schema/database prefix.</param>
/// <param name="IsFunctionCall">
/// True when the identifier is immediately followed by <c>(</c> — a table-valued function such as
/// <c>read_csv(...)</c> rather than a table. The strict policy refuses these categorically, which closes
/// the whole file-reading family without having to enumerate it.
/// </param>
/// <param name="Offset">Index of the reference in the original SQL.</param>
/// <param name="Length">Length of the reference in the original SQL.</param>
/// <param name="ResolvedTable">
/// The catalog table this matched, in the catalog's own casing, or null when it matched nothing.
/// </param>
/// <param name="Alias">
/// The alias written after the reference (<c>FROM orders o</c> or <c>FROM orders AS o</c>), or null when
/// there is none. Needed by the live-source rewriter: replacing the reference with a derived table means
/// emitting an alias, and emitting a second one alongside the caller's is a syntax error.
/// </param>
/// <param name="FactorEnd">
/// End of the whole table factor in the original SQL — past the alias when there is one, otherwise
/// <c>Offset + Length</c>. This, not <c>Length</c>, is what a rewrite must replace.
/// </param>
public sealed record TableReferenceMatch(
    string Raw,
    string Cleaned,
    string Name,
    bool IsQualified,
    bool IsFunctionCall,
    int Offset,
    int Length,
    string? ResolvedTable = null,
    string? Alias = null,
    int? FactorEnd = null)
{
    /// <summary>End of the table factor, falling back to the reference itself when no alias was seen.</summary>
    public int FactorEndOrSelf => FactorEnd ?? Offset + Length;
}

/// <summary>Everything both policies need, gathered in one pass over the SQL and one read of the catalog.</summary>
public sealed class SqlTableResolution
{
    /// <summary>
    /// Tables the user may read, or <c>null</c> when they may read all of them (dataset owner, an Admin
    /// share, or a share with no table restriction). Straight from
    /// <see cref="IDatasetService.GetAccessibleTablesAsync"/>; an empty set means no access at all.
    /// </summary>
    public HashSet<string>? AccessibleTables { get; init; }

    /// <summary>Every table in the dataset, in catalog casing.</summary>
    public List<string> AllTables { get; init; } = new();

    /// <summary>Every table reference found in the SQL, in the order they appear.</summary>
    public List<TableReferenceMatch> References { get; init; } = new();

    /// <summary>Names declared as CTEs by this query, which are never table references.</summary>
    public HashSet<string> CteNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the dataset's table list could not be read at all. Callers must fail closed: an empty
    /// table list is indistinguishable from "no tables exist", and treating it as "nothing to restrict"
    /// would turn a transient storage error into an access-control bypass.
    /// </summary>
    public bool SchemaReadFailed { get; init; }

    /// <summary>The error behind <see cref="SchemaReadFailed"/>, for logging.</summary>
    public string? SchemaError { get; init; }

    /// <summary>
    /// The permissive policy's answer: the first referenced table that is a known dataset table outside
    /// the user's allow-list, or null when the query is clear. Preserves the workbench's long-standing
    /// behaviour bit-for-bit.
    /// </summary>
    public string? FirstDisallowedKnownTable { get; init; }

    /// <summary>Lexical problem found while scanning the SQL (unterminated literal or comment).</summary>
    public string? LexError { get; init; }

    /// <summary>True when the user may read <paramref name="table"/>.</summary>
    public bool Allows(string table) =>
        AccessibleTables is null || AccessibleTables.Contains(table);
}

public class SqlTableResolver : ISqlTableResolver
{
    private readonly IDatasetService _datasetService;
    private readonly IDuckdbService _duckdbService;
    private readonly IDatabaseTableService _databaseTableService;

    public SqlTableResolver(IDatasetService datasetService, IDuckdbService duckdbService,
        IDatabaseTableService databaseTableService)
    {
        _datasetService = datasetService;
        _duckdbService = duckdbService;
        _databaseTableService = databaseTableService;
    }

    public async Task<SqlTableResolution> ResolveAsync(Dataset dataset, string userId, string companyId,
        string sql, bool snapshotMode, CancellationToken ct = default)
    {
        var scan = SqlText.Scan(sql);
        var accessible = await _datasetService.GetAccessibleTablesAsync(dataset.Id!, userId);

        List<string> allTables;
        var schemaFailed = false;
        string? schemaError = null;
        try
        {
            if (dataset.SourceType == DatasetSourceType.External && !snapshotMode
                && !string.IsNullOrWhiteSpace(dataset.SourceEntityId))
            {
                var discovery = await _databaseTableService.DiscoverTablesAsync(dataset.SourceEntityId, companyId, ct);
                allTables = discovery.Tables.Select(t => t.FullName).ToList();
                if (!string.IsNullOrWhiteSpace(discovery.Error))
                {
                    schemaFailed = true;
                    schemaError = discovery.Error;
                }
            }
            else
            {
                allTables = (await _duckdbService.GetTablesAsync(dataset.Id!)).ToList();
            }
        }
        catch (Exception ex)
        {
            // GetTablesAsync throws FileNotFoundException when the dataset's DuckDB file has not been
            // created yet, and DiscoverTablesAsync can fault on a bad connection. Neither is a 500.
            allTables = new List<string>();
            schemaFailed = true;
            schemaError = ex.Message;
        }

        var references = ExtractReferences(scan.Masked, sql);
        var cteNames = ExtractCteNames(scan.Masked);

        // Resolve each reference against the catalog, preferring an exact match on the whole dotted name
        // (external tables are "schema.table") and falling back to the last part. Casing comes from the
        // catalog, never from what the caller wrote — DuckDB identifiers are case-insensitive but
        // case-preserving, so emitting the caller's spelling can produce a column that does not exist.
        var byFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byLastPart = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
        {
            byFullName[t] = t;
            var lastPart = t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
            // First writer wins, so an ambiguous bare name resolves deterministically.
            if (!byLastPart.ContainsKey(lastPart)) byLastPart[lastPart] = t;
        }

        var resolved = references
            .Select(r =>
            {
                if (r.IsFunctionCall || cteNames.Contains(r.Name)) return r;
                if (byFullName.TryGetValue(r.Cleaned, out var full)) return r with { ResolvedTable = full };
                if (!r.IsQualified && byLastPart.TryGetValue(r.Name, out var bare)) return r with { ResolvedTable = bare };
                return r;
            })
            .ToList();

        return new SqlTableResolution
        {
            AccessibleTables = accessible,
            AllTables = allTables,
            References = resolved,
            CteNames = cteNames,
            SchemaReadFailed = schemaFailed,
            SchemaError = schemaError,
            LexError = scan.Error,
            FirstDisallowedKnownTable = FindDisallowedKnownTable(accessible, allTables, scan.Masked, sql)
        };
    }

    /// <summary>
    /// The workbench's original guard, preserved: only a reference matching a KNOWN disallowed table
    /// blocks, so CTEs, aliases and functions are never false-flagged.
    /// </summary>
    private static string? FindDisallowedKnownTable(HashSet<string>? accessible, List<string> allTables,
        string masked, string original)
    {
        if (accessible == null) return null; // full access — no guard needed

        var disallowed = allTables.Where(t => !accessible.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (disallowed.Count == 0) return null;

        return LegacyReferencedTables(masked, original).FirstOrDefault(r => disallowed.Contains(r));
    }

    // Matches the identifier following FROM/JOIN (handles quotes/brackets/backticks and schema.table).
    // Unchanged from the workbench's original, but now run over the masked text so a table name inside a
    // string literal or a comment is not treated as a reference.
    private static readonly Regex TableRefRegex =
        new(@"\b(?:from|join)\s+([A-Za-z0-9_\.""\[\]`]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<string> LegacyReferencedTables(string masked, string original)
    {
        foreach (Match m in TableRefRegex.Matches(masked))
        {
            var g = m.Groups[1];
            // Read the real text: the mask preserves offsets, so this is the same span of the original.
            var raw = original.Substring(g.Index, g.Length);
            var cleaned = raw.Replace("\"", "").Replace("[", "").Replace("]", "").Replace("`", "");
            if (!string.IsNullOrWhiteSpace(cleaned))
                yield return cleaned;
        }
    }

    /// <summary>Keywords that end a FROM list, so an alias scan does not swallow the next clause.</summary>
    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "FETCH", "WINDOW", "UNION", "EXCEPT",
        "INTERSECT", "ON", "USING", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL",
        "QUALIFY", "FOR", "INTO", "TABLESAMPLE", "USE", "WITH", "SELECT", "AS", "ASOF", "POSITIONAL", "ANTI", "SEMI"
    };

    /// <summary>
    /// Finds every table reference by scanning for FROM/JOIN/APPLY and then reading the comma-separated
    /// list of table factors that follows.
    /// </summary>
    /// <remarks>
    /// A single regex is not enough for the strict policy: <c>FROM orders, secret</c> matches only
    /// <c>orders</c>, which would let the second table through unchecked. Sub-selects need no recursion
    /// here — the scan sees the whole string, so an inner query's own FROM is found on its own.
    /// </remarks>
    public static List<TableReferenceMatch> ExtractReferences(string masked, string original)
    {
        var results = new List<TableReferenceMatch>();
        var n = masked.Length;

        for (var i = 0; i < n; i++)
        {
            if (!IsWordStart(masked, i)) continue;
            var word = ReadWord(masked, i, out var afterWord);
            if (word is not ("FROM" or "JOIN" or "APPLY"))
            {
                i = afterWord - 1;
                continue;
            }

            var p = afterWord;
            while (true)
            {
                p = SkipWhitespace(masked, p);
                if (p >= n) break;

                // Index in `results` of the reference just collected, so the alias found below can be
                // recorded against it. Stays -1 for a derived table / parenthesised join, which has no
                // entry of its own — its contents are scanned independently.
                var collectedIndex = -1;

                if (masked[p] == '(')
                {
                    // Derived table or a parenthesised join; its contents are scanned independently.
                    p = SkipBalancedParens(masked, p);
                }
                else
                {
                    var match = ReadIdentifierChain(masked, original, p);
                    if (match is null) break;
                    results.Add(match);
                    collectedIndex = results.Count - 1;
                    p = match.Offset + match.Length;
                    if (match.IsFunctionCall) p = SkipBalancedParens(masked, SkipWhitespace(masked, p));
                }

                // Optional alias, with or without AS, bare or quoted.
                string? aliasName = null;
                var afterAlias = SkipWhitespace(masked, p);
                if (afterAlias < n && IsWordStart(masked, afterAlias))
                {
                    var next = ReadWord(masked, afterAlias, out var afterNext);
                    if (string.Equals(next, "AS", StringComparison.OrdinalIgnoreCase))
                    {
                        var afterAs = SkipWhitespace(masked, afterNext);
                        if (afterAs < n && IsWordStart(masked, afterAs))
                        {
                            // Sliced from the ORIGINAL text, not taken from ReadWord's return: that
                            // upper-cases for keyword matching, and an alias must keep the caller's
                            // casing. Quoted identifiers are case-sensitive on ClickHouse and
                            // PostgreSQL, so re-emitting "sl" as "SL" would stop the caller's own
                            // column qualifiers binding.
                            ReadWord(masked, afterAs, out afterNext);
                            aliasName = original[afterAs..afterNext];
                        }
                        else if (afterAs < n && IsQuoteStart(masked, afterAs))
                        {
                            aliasName = ReadQuotedIdentifier(masked, original, afterAs, out afterNext);
                        }
                        p = afterNext;
                    }
                    else if (!ClauseKeywords.Contains(next))
                    {
                        // Same reason: `next` is upper-cased, the original span is not.
                        aliasName = original[afterAlias..afterNext];
                        p = afterNext;
                    }
                }
                else if (afterAlias < n && IsQuoteStart(masked, afterAlias))
                {
                    // A quoted alias with no AS (FROM orders "o"). Consumed explicitly because the bare-word
                    // branch above cannot see it, and a rewriter that left it in place would emit two
                    // aliases and produce invalid SQL.
                    aliasName = ReadQuotedIdentifier(masked, original, afterAlias, out var afterQuoted);
                    p = afterQuoted;
                }

                if (collectedIndex >= 0)
                    results[collectedIndex] = results[collectedIndex] with { Alias = aliasName, FactorEnd = p };
                // A parenthesised column alias list, e.g. "AS t(a, b)".
                var afterAliasCols = SkipWhitespace(masked, p);
                if (afterAliasCols < n && masked[afterAliasCols] == '(')
                    p = SkipBalancedParens(masked, afterAliasCols);

                var afterFactor = SkipWhitespace(masked, p);
                if (afterFactor < n && masked[afterFactor] == ',')
                {
                    p = afterFactor + 1;
                    continue;
                }
                break;
            }

            // Resume immediately after the FROM/JOIN keyword, NOT after the factors just consumed.
            // Advancing past them would skip a nested query's own FROM — so
            // `SELECT * FROM (SELECT * FROM secret) x` would report no tables at all, and the strict
            // policy would find nothing to check. Re-scanning the factor text is harmless: the
            // identifiers in it are not FROM/JOIN keywords, so nothing is collected twice.
            i = afterWord - 1;
        }

        return results;
    }

    /// <summary>
    /// Names this query declares as CTEs. They shadow (or simply are not) tables, so they must never be
    /// checked against the catalog — and on the secured path a collision with a secured relation is a
    /// hard error rather than something to reason about.
    /// </summary>
    public static HashSet<string> ExtractCteNames(string masked)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = masked.Length;

        var i = SkipWhitespace(masked, 0);
        if (i >= n || !IsWordStart(masked, i)) return names;
        if (!string.Equals(ReadWord(masked, i, out var afterWith), "WITH", StringComparison.OrdinalIgnoreCase))
            return names;

        var p = SkipWhitespace(masked, afterWith);
        if (p < n && IsWordStart(masked, p))
        {
            var maybeRecursive = ReadWord(masked, p, out var afterRecursive);
            if (string.Equals(maybeRecursive, "RECURSIVE", StringComparison.OrdinalIgnoreCase))
                p = SkipWhitespace(masked, afterRecursive);
        }

        while (p < n)
        {
            var chain = ReadIdentifierChain(masked, masked, p);
            if (chain is null) break;
            p = chain.Offset + chain.Length;

            // Optional column list: name(a, b) AS ( ... )
            p = SkipWhitespace(masked, p);
            if (p < n && masked[p] == '(') p = SkipBalancedParens(masked, p);

            p = SkipWhitespace(masked, p);
            if (p >= n || !IsWordStart(masked, p)) break;
            if (!string.Equals(ReadWord(masked, p, out var afterAs), "AS", StringComparison.OrdinalIgnoreCase))
                break;

            p = SkipWhitespace(masked, afterAs);
            // DuckDB and Postgres allow MATERIALIZED / NOT MATERIALIZED here.
            while (p < n && IsWordStart(masked, p))
            {
                var hint = ReadWord(masked, p, out var afterHint);
                if (!string.Equals(hint, "MATERIALIZED", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(hint, "NOT", StringComparison.OrdinalIgnoreCase)) break;
                p = SkipWhitespace(masked, afterHint);
            }

            if (p >= n || masked[p] != '(') break;
            names.Add(chain.Name);
            p = SkipBalancedParens(masked, p);

            p = SkipWhitespace(masked, p);
            if (p < n && masked[p] == ',')
            {
                p = SkipWhitespace(masked, p + 1);
                continue;
            }
            break;
        }

        return names;
    }

    /// <summary>
    /// Every dotted identifier chain in the text, wherever it appears — not only in table position.
    /// </summary>
    /// <remarks>
    /// Deliberately position-blind, which is what makes it useful as a <b>verification</b> pass rather
    /// than an extraction one. <see cref="ExtractReferences"/> decides what to secure and has to
    /// understand table positions to do it; this one only has to notice that a name is present at all, so
    /// it cannot miss a table reference sitting in a construct the position logic does not model. The
    /// live-source rewriter uses it to prove no route to a base table survived the rewrite.
    /// </remarks>
    public static List<TableReferenceMatch> ScanAllIdentifierChains(string masked, string original)
    {
        var chains = new List<TableReferenceMatch>();
        var i = 0;

        while (i < masked.Length)
        {
            if (!IsWordStart(masked, i) && !IsQuoteStart(masked, i)) { i++; continue; }

            var chain = ReadIdentifierChain(masked, original, i);
            if (chain is null) { i++; continue; }

            chains.Add(chain);
            // Always advance: a zero-length read would otherwise spin here forever.
            i = chain.Length > 0 ? chain.Offset + chain.Length : i + 1;
        }

        return chains;
    }

    private static bool IsWordStart(string s, int i) =>
        i < s.Length && (char.IsLetter(s[i]) || s[i] == '_');

    /// <summary>True when a quoted identifier opens at <paramref name="i"/> in any engine's style.</summary>
    private static bool IsQuoteStart(string s, int i) =>
        i < s.Length && s[i] is '"' or '[' or '`';

    /// <summary>
    /// Reads one quoted identifier, returning its unquoted text and the index just past the closing
    /// delimiter. Doubled delimiters are an escaped delimiter, matching
    /// <see cref="ReadIdentifierChain"/>. On an unterminated quote the whole span is consumed and null
    /// returned — the scan has already flagged the input as malformed, and the caller must not treat the
    /// remainder as meaningful.
    /// </summary>
    private static string? ReadQuotedIdentifier(string masked, string original, int start, out int end)
    {
        var close = masked[start] == '[' ? ']' : masked[start];
        var i = start + 1;
        var sb = new StringBuilder();

        while (i < masked.Length)
        {
            if (masked[i] == close)
            {
                if (i + 1 < masked.Length && masked[i + 1] == close) { sb.Append(close); i += 2; continue; }
                end = i + 1;
                return sb.ToString();
            }
            sb.Append(original[i]);
            i++;
        }

        end = masked.Length;
        return null;
    }

    private static string ReadWord(string s, int i, out int after)
    {
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        after = i;
        return s[start..i].ToUpperInvariant();
    }

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int SkipBalancedParens(string s, int i)
    {
        if (i >= s.Length || s[i] != '(') return i;
        var depth = 0;
        for (; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        return s.Length;
    }

    /// <summary>
    /// Reads a dotted identifier chain at <paramref name="start"/>, e.g. <c>orders</c>,
    /// <c>main.orders</c>, <c>[db].[dbo].[orders]</c>. Returns null when there is no identifier there.
    /// </summary>
    private static TableReferenceMatch? ReadIdentifierChain(string masked, string original, int start)
    {
        var parts = new List<string>();
        var i = start;
        var n = masked.Length;

        while (i < n)
        {
            if (masked[i] is '"' or '[' or '`')
            {
                var close = masked[i] == '[' ? ']' : masked[i];
                var from = ++i;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (masked[i] == close)
                    {
                        if (i + 1 < n && masked[i + 1] == close) { sb.Append(close); i += 2; continue; }
                        break;
                    }
                    sb.Append(original[i]);
                    i++;
                }
                if (i >= n) return null; // unterminated; the scan already flagged it
                i++; // closing delimiter
                parts.Add(sb.ToString());
                _ = from;
            }
            else if (char.IsLetter(masked[i]) || masked[i] is '_' or '#' or '@')
            {
                var from = i;
                while (i < n && (char.IsLetterOrDigit(masked[i]) || masked[i] is '_' or '#' or '@' or '$')) i++;
                parts.Add(original[from..i]);
            }
            else
            {
                break;
            }

            if (i < n && masked[i] == '.') { i++; continue; }
            break;
        }

        if (parts.Count == 0) return null;

        var length = i - start;
        var raw = original.Substring(start, length);
        var isFunctionCall = SkipWhitespace(masked, i) < n && masked[SkipWhitespace(masked, i)] == '(';

        return new TableReferenceMatch(
            Raw: raw,
            Cleaned: string.Join(".", parts),
            Name: parts[^1],
            IsQualified: parts.Count > 1,
            IsFunctionCall: isFunctionCall,
            Offset: start,
            Length: length);
    }
}

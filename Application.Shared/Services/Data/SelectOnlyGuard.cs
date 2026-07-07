using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Application.Shared.Services.Data;

/// <summary>
/// Defense-in-depth check that a piece of SQL is a single read-only statement before it is executed
/// or persisted as a dashboard widget. This is NOT the only boundary — the DuckDB/ClickHouse execution
/// paths run read-only with timeouts and row caps regardless — but it rejects obviously-unsafe model
/// output early (DDL/DML, stacked statements, comment-hidden second statements).
/// </summary>
public static class SelectOnlyGuard
{
    private static readonly Regex BlockComments = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LineComments = new(@"--[^\n]*", RegexOptions.Compiled);

    /// <summary>Returns true when <paramref name="sql"/> is a single SELECT/WITH statement.</summary>
    public static bool IsSafeSelect(string? sql, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sql))
        {
            error = "Query is empty.";
            return false;
        }

        // Strip comments so a second statement can't hide behind `-- ` or `/* */`.
        var cleaned = LineComments.Replace(BlockComments.Replace(sql, " "), " ").Trim();

        // Reject stacked statements. Allow a single trailing semicolon.
        var statements = cleaned.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (statements.Count != 1)
        {
            error = "Only a single SQL statement is allowed.";
            return false;
        }

        var firstWord = new string(statements[0].TrimStart().TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (firstWord is not ("SELECT" or "WITH"))
        {
            error = "Only read-only SELECT queries are allowed.";
            return false;
        }

        return true;
    }
}

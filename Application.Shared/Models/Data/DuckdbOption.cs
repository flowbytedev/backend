using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Shared.Models.Data;

public class DuckdbOption
{
    public string DuckdbFilePath { get; set; } = default!;

    /// <summary>
    /// Directory DuckDB installs/caches loadable extensions (e.g. the <c>excel</c> extension) into.
    /// Set this explicitly so extension installs do NOT fall back to the running account's home
    /// directory — under a Windows service / IIS app pool identity that resolves to
    /// <c>C:\Windows\System32\config\systemprofile</c>, which is locked down and makes
    /// <c>INSTALL excel</c> fail. When unset, defaults to a <c>.duckdb_ext</c> folder next to the
    /// DuckDB data files (see <see cref="ResolveExtensionDirectory"/>).
    /// </summary>
    public string? ExtensionDirectory { get; set; }

    /// <summary>The effective extension directory: the configured value, or a folder beside the data files.</summary>
    public string ResolveExtensionDirectory() =>
        string.IsNullOrWhiteSpace(ExtensionDirectory)
            ? System.IO.Path.Combine(DuckdbFilePath, ".duckdb_ext")
            : ExtensionDirectory;

    // ---- Ad-hoc / build limits ---------------------------------------------------------------------
    // These were private consts in DuckdbService (5000 rows, 60s). They are config here because the SAME
    // code path serves two very different callers: an interactive workbench query, where 60s is a
    // reasonable "your query is too slow" signal, and an unattended materialization, where it is simply a
    // wall. Scheduled work genuinely hits that wall today — the SqlQuery ingestion kind
    // (IngestionService.RunSourceAsync) and scheduled notebook cells (QueryNotebookService.RunCellAsync)
    // both route through the build paths below.
    //
    // Every value is nullable and every resolver falls back to the original constant, so an appsettings
    // file that says nothing about them behaves exactly as before.

    /// <summary>Timeout for interactive ad-hoc SQL (the query workbench). Defaults to 60s.</summary>
    public int? QueryTimeoutSeconds { get; set; }

    /// <summary>
    /// Timeout for unattended materialization — CREATE TABLE/VIEW AS SELECT and query-into-table.
    /// Separate from <see cref="QueryTimeoutSeconds"/> precisely because these run from a schedule with
    /// nobody watching, so the interactive limit is the wrong instinct. Defaults to 60s (unchanged).
    /// </summary>
    public int? BuildTimeoutSeconds { get; set; }

    /// <summary>
    /// Hard ceiling on rows an ad-hoc query returns, regardless of a requested MaxRows — keeps a runaway
    /// "SELECT *" from materializing an entire table into memory. Defaults to 5000.
    /// </summary>
    public int? MaxAdHocRows { get; set; }

    private const int DefaultQueryTimeoutSeconds = 60;
    private const int DefaultBuildTimeoutSeconds = 60;
    private const int DefaultMaxAdHocRows = 5000;

    public int ResolveQueryTimeoutSeconds() => Positive(QueryTimeoutSeconds, DefaultQueryTimeoutSeconds);
    public int ResolveBuildTimeoutSeconds() => Positive(BuildTimeoutSeconds, DefaultBuildTimeoutSeconds);
    public int ResolveMaxAdHocRows() => Positive(MaxAdHocRows, DefaultMaxAdHocRows);

    // A configured 0 or negative would mean "cancel immediately" / "return nothing" — always a typo, and
    // one that would look like a hung or empty dataset rather than a bad setting. Treat it as unset.
    private static int Positive(int? configured, int fallback) =>
        configured is > 0 ? configured.Value : fallback;
}


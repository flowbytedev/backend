using System.Text;
using Application.Shared.Enums;
using Application.Shared.Models.Data;

namespace Application.Shared.Services.Data;

/// <summary>
/// Enforces column masking and row-level security against a <b>live external source</b> by replacing
/// every reference to a dataset table with a secured derived table, then refusing the query unless it can
/// prove no route to a base table survived.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the CTE approach <see cref="SecuredSqlBuilder"/> uses?</b> That works by shadowing: a CTE
/// named <c>orders</c> takes the place of the table <c>orders</c>, so the engine's own name resolution
/// covers every occurrence — including ones in nested constructs the reference scanner never visited.
/// A live source's tables are <c>{schema}.{name}</c>, and a CTE can neither be named that nor shadow it,
/// so that mechanism is simply unavailable here. It also needs the engine to support CTEs at all, which
/// MySQL before 8.0 does not.
/// </para>
/// <para>
/// <b>What replaces the shadowing guarantee.</b> Substituting at each reference site means the safety of
/// the result depends on having found every reference — a weaker position, since the scanner is not a
/// SQL parser. So the rewrite is followed by a verification pass over the rewritten text, outside the
/// spans this class injected, that refuses if any dotted chain still names a dataset table, or if a bare
/// table name appears where the source's default schema could resolve it. A reference the scanner missed
/// is still <i>literally present as text</i>, and the verification pass is position-blind, so it does not
/// share the scanner's blind spots. Anything unproven is refused, never approximated.
/// </para>
/// <para>
/// This is still the weaker of the two enforcement modes and is chosen deliberately
/// (<see cref="RlsEnforcementMode.Rewrite"/>), for sources whose engine has no per-user row security or
/// where nobody can provision it. <see cref="RlsEnforcementMode.Native"/> is preferred wherever available
/// because it puts the boundary in the engine rather than in a text transformation.
/// </para>
/// </remarks>
public static class LiveSourceSqlRewriter
{
    /// <summary>One live table reduced to what the acting user may see of it.</summary>
    /// <param name="CatalogTable">The catalog's name for it, e.g. <c>sales_dataset.sales_line</c>.</param>
    /// <param name="Columns">Granted columns, in catalog casing. Empty is invalid.</param>
    /// <param name="Predicates">Row filters to apply inside the derived table.</param>
    public sealed record LiveRelation(
        string CatalogTable,
        IReadOnlyList<string> Columns,
        IReadOnlyList<SecuredSqlBuilder.RlsPredicate> Predicates)
    {
        /// <summary>The last dotted part — the alias used when the caller supplied none.</summary>
        public string BareName =>
            CatalogTable.Contains('.') ? CatalogTable[(CatalogTable.LastIndexOf('.') + 1)..] : CatalogTable;
    }

    /// <summary>
    /// Rewrites <paramref name="userSql"/> so every dataset table it names is read through a secured
    /// derived table. Returns false with an <paramref name="errorCode"/> from
    /// <see cref="PublicSqlErrorCodes"/> when the result cannot be proven safe, in which case the caller
    /// must run nothing at all.
    /// </summary>
    /// <param name="references">
    /// Every reference the resolver found, in any order. Those whose <c>ResolvedTable</c> is null are
    /// ignored here — the caller has already refused unknown tables.
    /// </param>
    /// <param name="relations">Secured definition per catalog table name, case-insensitive.</param>
    /// <param name="allCatalogTables">
    /// Every table in the dataset, for the verification pass. Must be the complete set: a table missing
    /// from it is a table the pass cannot notice. Callers already refuse on partial schema reads
    /// (<c>schema_unavailable</c>), which is what makes this safe to rely on.
    /// </param>
    public static bool TryBuild(
        string userSql,
        IReadOnlyList<TableReferenceMatch> references,
        IReadOnlyDictionary<string, LiveRelation> relations,
        IReadOnlyCollection<string> allCatalogTables,
        DataSourceType dialect,
        out string effectiveSql,
        out string? error,
        out string? errorCode)
    {
        effectiveSql = string.Empty;
        error = null;
        errorCode = null;

        var targets = references
            .Where(r => r.ResolvedTable is not null)
            .OrderByDescending(r => r.Offset)
            .ToList();

        if (targets.Count == 0)
        {
            // Nothing of the dataset's is referenced (a query over the caller's own CTEs, say). There is
            // nothing to secure, but the verification pass still runs below on the unchanged text so that
            // a reference the extractor missed entirely cannot slip through as "nothing to do".
            effectiveSql = userSql;
            return VerifyNoResidualAccess(effectiveSql, Array.Empty<(int, int)>(), allCatalogTables,
                out error, out errorCode);
        }

        // Two references to the same table must not disagree about what is granted.
        foreach (var target in targets)
        {
            if (!relations.TryGetValue(target.ResolvedTable!, out var relation))
            {
                error = $"Internal: no secured definition was built for table '{target.ResolvedTable}'.";
                errorCode = PublicSqlErrorCodes.SecurityNotEnforceable;
                return false;
            }

            if (relation.Columns.Count == 0)
            {
                error = $"You have no readable columns in table '{relation.CatalogTable}'.";
                errorCode = PublicSqlErrorCodes.ColumnNotPermitted;
                return false;
            }
        }

        // Overlapping factors would corrupt the output. Cannot happen from a single scan, but the cost of
        // checking is nil next to the cost of being wrong.
        for (var i = 1; i < targets.Count; i++)
        {
            if (targets[i].FactorEndOrSelf > targets[i - 1].Offset)
            {
                error = "Your query could not be secured because its table references overlap.";
                errorCode = PublicSqlErrorCodes.SecurityNotEnforceable;
                return false;
            }
        }

        // Rewrite back-to-front so each replacement leaves earlier offsets valid.
        var sb = new StringBuilder(userSql);
        var injected = new List<(int Start, int Length)>();

        foreach (var target in targets)
        {
            var relation = relations[target.ResolvedTable!];
            var alias = string.IsNullOrWhiteSpace(target.Alias) ? relation.BareName : target.Alias!;
            var factor = RenderSecuredFactor(relation, alias, dialect);

            var start = target.Offset;
            var length = target.FactorEndOrSelf - target.Offset;
            sb.Remove(start, length);
            sb.Insert(start, factor);

            // Spans recorded against the FINAL text. Because replacement runs back-to-front, everything
            // already recorded sits after this one and its offset is unaffected by this edit.
            injected.Add((start, factor.Length));
        }

        effectiveSql = sb.ToString();
        return VerifyNoResidualAccess(effectiveSql, injected, allCatalogTables, out error, out errorCode);
    }

    /// <summary>
    /// Renders one secured derived table: <c>(SELECT granted FROM real WHERE filter) AS alias</c>.
    /// </summary>
    /// <remarks>
    /// The alias is not optional padding — MySQL and SQL Server both reject an unaliased derived table.
    /// Reusing the caller's own alias when they wrote one keeps their column qualifiers binding, and
    /// falling back to the bare table name does the same for callers who wrote none.
    /// </remarks>
    private static string RenderSecuredFactor(LiveRelation relation, string alias, DataSourceType dialect)
    {
        var columns = string.Join(",", relation.Columns.Select(c => Quote(dialect, c)));

        var sb = new StringBuilder();
        sb.Append("(SELECT ").Append(columns)
          .Append(" FROM ").Append(QualifiedSource(relation.CatalogTable, dialect));

        if (relation.Predicates.Count > 0)
        {
            sb.Append(" WHERE ");
            sb.Append(string.Join(" AND ", relation.Predicates.Select(p => RenderPredicate(p, dialect))));
        }

        sb.Append(") AS ").Append(Quote(dialect, alias));
        return sb.ToString();
    }

    /// <summary>
    /// Quotes each dotted part of the catalog name separately for the target engine.
    /// </summary>
    /// <remarks>
    /// Not <c>SqlTypeMapper.QualifiedTable</c>: that drops the schema for MySQL and ClickHouse, because in
    /// a pipeline's vocabulary their "schema" slot is the database. Here the catalog name came from table
    /// discovery as <c>{schema}.{name}</c>, where for those engines that first part <i>is</i> the database
    /// and removing it would address a different table — or none.
    /// </remarks>
    private static string QualifiedSource(string catalogTable, DataSourceType dialect) =>
        string.Join(".", catalogTable.Split('.').Select(part => Quote(dialect, part)));

    /// <summary>
    /// Renders one row filter. An empty allowed-value set becomes <c>1 = 0</c>: the grant says this user
    /// may see no values of the column, so they may see no rows. Mirrors
    /// <see cref="SecuredSqlBuilder"/> deliberately — the two modes must filter identically or the same
    /// grant would mean different things on the two layers of one dataset.
    /// </summary>
    private static string RenderPredicate(SecuredSqlBuilder.RlsPredicate predicate, DataSourceType dialect)
    {
        if (predicate.AllowedValues.Count == 0) return "1 = 0";

        var column = Quote(dialect, predicate.ColumnName);
        var allNumeric = predicate.AllowedValues.All(SecuredSqlBuilder.IsNumericLiteral);
        var rendered = predicate.AllowedValues.Select(v =>
            allNumeric ? v.Trim() : SecuredSqlBuilder.QuoteLiteral(v));
        return $"{column} IN ({string.Join(",", rendered)})";
    }

    /// <summary>
    /// Identifier quoting for the target engine. Delegates to the pipeline mapper, which already knows
    /// that SQL Server takes brackets and MySQL takes backticks — a plain double quote would be a string
    /// literal on MySQL, not an identifier, and the statement would not even parse.
    /// </summary>
    private static string Quote(DataSourceType dialect, string identifier) =>
        Pipelines.SqlTypeMapper.Quote(dialect, identifier);

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// Proves that the rewritten SQL has no route to a base table left, outside the spans we injected.
    /// </summary>
    /// <remarks>
    /// This is the pass that carries the safety argument, so it is written to over-refuse. Two things are
    /// rejected anywhere outside an injected span:
    /// <list type="bullet">
    /// <item>A dotted chain naming a dataset table (<c>sales_dataset.sales_line</c>, or
    /// <c>db.sales_dataset.sales_line</c>). After a complete rewrite none can legitimately remain, so one
    /// that does is a reference the scanner missed.</item>
    /// <item>A bare, undotted identifier equal to a dataset table's own name. On engines that resolve
    /// bare names against the connection's default schema or database, such a reference reads live data
    /// rather than erroring. A column qualifier like <c>sales_line.amount</c> is a two-part chain and is
    /// not affected; a lone <c>sales_line</c> is refused.</item>
    /// </list>
    /// The false-positive cost is a refused query when a column happens to be named after a table, with
    /// a message naming the identifier so the model can alias around it. That is the right way for this
    /// to be wrong.
    /// </remarks>
    private static bool VerifyNoResidualAccess(
        string rewritten,
        IReadOnlyCollection<(int Start, int Length)> injected,
        IReadOnlyCollection<string> allCatalogTables,
        out string? error,
        out string? errorCode)
    {
        error = null;
        errorCode = null;

        var scan = SqlText.Scan(rewritten);
        if (scan.Error is not null)
        {
            error = $"The rewritten query could not be re-read to verify it: {scan.Error}";
            errorCode = PublicSqlErrorCodes.SecurityNotEnforceable;
            return false;
        }

        // Blank the spans we injected: they legitimately contain the real qualified table names.
        var masked = scan.Masked.ToCharArray();
        foreach (var (start, length) in injected)
        {
            var end = Math.Min(masked.Length, start + length);
            for (var i = Math.Max(0, start); i < end; i++) masked[i] = ' ';
        }
        var remainder = new string(masked);

        var bareNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in allCatalogTables)
            bareNames.Add(table.Contains('.') ? table[(table.LastIndexOf('.') + 1)..] : table);

        foreach (var chain in SqlTableResolver.ScanAllIdentifierChains(remainder, rewritten))
        {
            var cleaned = chain.Cleaned;

            // Qualified: equal to a catalog name, or ending in one (a database-qualified three-part form).
            foreach (var table in allCatalogTables)
            {
                if (!table.Contains('.')) continue;
                if (cleaned.Equals(table, StringComparison.OrdinalIgnoreCase)
                    || cleaned.EndsWith("." + table, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Reference '{chain.Raw}' still addresses table '{table}' directly, so column and "
                            + "row security could not be guaranteed for it. Nothing was run. Use the table "
                            + "name on its own.";
                    errorCode = PublicSqlErrorCodes.SecurityNotEnforceable;
                    return false;
                }
            }

            // Bare: a lone identifier that is a table's own name.
            if (!cleaned.Contains('.') && bareNames.Contains(cleaned))
            {
                error = $"Identifier '{chain.Raw}' is a table name used outside a secured reference, so "
                        + "column and row security could not be guaranteed. Nothing was run. Alias it, or "
                        + "reference the table in a FROM or JOIN clause.";
                errorCode = PublicSqlErrorCodes.SecurityNotEnforceable;
                return false;
            }
        }

        return true;
    }
}

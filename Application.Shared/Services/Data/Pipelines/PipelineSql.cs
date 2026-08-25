using System.Text;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Compiles a transform node into the SELECT that implements it. Every transform in this feature is SQL
/// underneath — that is the whole point of D1 — so this class is where the feature's actual behaviour lives.
/// <para>
/// Deliberately pure and static: no connection, no I/O, no state. It takes a node's config plus the columns
/// its input relation actually has, and returns either SQL or a reason. That makes the riskiest logic in
/// the feature testable without a database, which matters in a repo with no test project.
/// </para>
/// <para>
/// <b>Injection posture.</b> Identifiers are quoted by <see cref="Q"/> and string values by
/// <see cref="Lit"/>, so anything the editor produces from a picker or a grid is safe by construction.
/// Three fields are free-form SQL <em>by design</em> — a filter condition, a computed-column expression,
/// and the SQL step — because a no-SQL builder that cannot express <c>CASE WHEN</c> is not useful. Those are
/// contained two ways: the whole generated statement is re-checked with
/// <see cref="SelectOnlyGuard"/> before execution (so a semicolon cannot stack a second statement), and the
/// feature is DATA_ADMIN-gated, the same bar as the existing ingestion and workbench pages.
/// </para>
/// </summary>
public static partial class PipelineSql
{
    /// <summary>
    /// Cast targets allowed in a mapping. A whitelist rather than validation, because this value is
    /// interpolated into SQL and there is no way to parameterize a type name.
    /// </summary>
    private static readonly HashSet<string> AllowedCastTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VARCHAR", "TEXT", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "HUGEINT",
        "DOUBLE", "FLOAT", "REAL", "DECIMAL", "NUMERIC", "BOOLEAN",
        "DATE", "TIMESTAMP", "TIME", "TIMESTAMPTZ", "INTERVAL",
        "UUID", "JSON", "BLOB"
    };

    /// <summary>Quotes an identifier for DuckDB, doubling any embedded quote.</summary>
    public static string Q(string identifier) => "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";

    /// <summary>Quotes a string literal, doubling any embedded apostrophe.</summary>
    public static string Lit(string? value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    /// <summary>
    /// Builds the SELECT for a transform node.
    /// </summary>
    /// <param name="node">The node to compile.</param>
    /// <param name="inputs">Input port -&gt; (relation name, its columns), as resolved by the engine.</param>
    public static SqlBuildResult Build(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs) =>
        node.Type switch
        {
            PipelineNodeTypes.TransformMap => Map(node, inputs),
            PipelineNodeTypes.TransformFilter => Filter(node, inputs),
            PipelineNodeTypes.TransformCompute => Compute(node, inputs),
            PipelineNodeTypes.TransformJoin => Join(node, inputs),
            PipelineNodeTypes.TransformUnion => Union(node, inputs),
            PipelineNodeTypes.TransformDedupe => Dedupe(node, inputs),
            PipelineNodeTypes.TransformAggregate => Aggregate(node, inputs),
            PipelineNodeTypes.TransformSql => RawSql(node, inputs),

            // Round two. Each of these was already possible in a transform.sql step; these make them
            // clickable, with column names validated against the incoming schema.
            PipelineNodeTypes.TransformSort => Sort(node, inputs),
            PipelineNodeTypes.TransformRank => Rank(node, inputs),
            PipelineNodeTypes.TransformSurrogateKey => SurrogateKey(node, inputs),
            PipelineNodeTypes.TransformWindow => Window(node, inputs),
            PipelineNodeTypes.TransformFill => Fill(node, inputs),
            PipelineNodeTypes.TransformText => Text(node, inputs),
            PipelineNodeTypes.TransformSplit => Split(node, inputs),
            PipelineNodeTypes.TransformFlatten => Flatten(node, inputs),
            PipelineNodeTypes.TransformParse => Parse(node, inputs),
            PipelineNodeTypes.TransformPivot => Pivot(node, inputs),
            PipelineNodeTypes.TransformUnpivot => Unpivot(node, inputs),
            _ => SqlBuildResult.Fail($"'{node.Type}' is not a transform step.", PipelineErrorType.Invalid)
        };

    // ------------------------------------------------------------------ map

    private static SqlBuildResult Map(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        if (node.Config?["columns"] is not JsonObject columns || columns.Count == 0)
            return SqlBuildResult.Fail("This step has no column mapping.", PipelineErrorType.Invalid);

        var available = input.ColumnLookup;
        var keepUnmapped = Bool(node.Config, "keepUnmapped");
        var drop = StringList(node.Config, "drop");

        var projections = new List<string>();
        var mappedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (outName, spec) in columns)
        {
            if (spec is not JsonObject map)
                return SqlBuildResult.Fail($"The mapping for '{outName}' is malformed.", PipelineErrorType.Invalid);

            var castType = Str(map, "cast");
            if (castType is not null && !AllowedCastTypes.Contains(castType))
            {
                return SqlBuildResult.Fail(
                    $"'{castType}' is not a type this step can cast to. Allowed: {string.Join(", ", AllowedCastTypes.OrderBy(t => t))}.",
                    PipelineErrorType.Invalid);
            }

            string expression;
            if (map["const"] is { } constant)
            {
                // A constant is a value, so it is always a literal — never interpolated raw.
                expression = constant is JsonValue v && v.TryGetValue<string>(out var text)
                    ? Lit(text)
                    : Lit(constant.ToString());
            }
            else
            {
                var source = Str(map, "source");
                if (string.IsNullOrWhiteSpace(source))
                    return SqlBuildResult.Fail($"'{outName}' has neither a source column nor a value.",
                        PipelineErrorType.Invalid);

                // The schema-drift check, at the point where it is an error rather than a warning. The
                // available list is in the message because "column not found" without it is a guessing game.
                if (!available.TryGetValue(source!, out var actual))
                {
                    return SqlBuildResult.Fail(
                        $"Column '{source}' does not exist in the incoming data. Available: " +
                        $"{string.Join(", ", input.Columns.Select(c => c.Name))}.",
                        PipelineErrorType.SchemaDrift);
                }

                mappedSources.Add(actual);
                expression = Q(actual);
            }

            if (castType is not null) expression = $"CAST({expression} AS {castType})";
            projections.Add($"{expression} AS {Q(outName)}");
        }

        if (keepUnmapped)
        {
            // Everything not explicitly mapped, not dropped, and not consumed as a rename source. Built as
            // an explicit list rather than SELECT * EXCLUDE so the generated SQL states exactly what it
            // keeps — which is what makes the step's SqlText useful when the numbers look wrong.
            var dropSet = new HashSet<string>(drop, StringComparer.OrdinalIgnoreCase);
            var outNames = new HashSet<string>(columns.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);

            var passthrough = input.Columns
                .Where(c => !dropSet.Contains(c.Name)
                            && !mappedSources.Contains(c.Name)
                            && !outNames.Contains(c.Name))
                .Select(c => Q(c.Name));

            projections.InsertRange(0, passthrough);
        }

        return SqlBuildResult.Ok($"SELECT {string.Join(", ", projections)} FROM {Q(input.Relation)}");
    }

    // --------------------------------------------------------------- filter

    private static SqlBuildResult Filter(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var where = Str(node.Config, "where");
        if (string.IsNullOrWhiteSpace(where))
            return SqlBuildResult.Fail("This step has no condition.", PipelineErrorType.Invalid);

        return SqlBuildResult.Ok($"SELECT * FROM {Q(input.Relation)} WHERE ({where})");
    }

    // -------------------------------------------------------------- compute

    private static SqlBuildResult Compute(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var columns = node.Config?["columns"];
        var additions = new List<string>();

        // Accepts both an object map and a list of {name, expression} rows, because the inspector's grid
        // and a hand-written YAML file naturally produce different shapes for the same idea.
        switch (columns)
        {
            case JsonObject map:
                foreach (var (name, expr) in map)
                {
                    var text = (expr as JsonValue)?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(text))
                        return SqlBuildResult.Fail($"'{name}' has no expression.", PipelineErrorType.Invalid);
                    additions.Add($"({text}) AS {Q(name)}");
                }
                break;

            case JsonArray rows:
                foreach (var row in rows)
                {
                    var name = Str(row as JsonObject, "name");
                    var text = Str(row as JsonObject, "expression");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(text))
                        return SqlBuildResult.Fail("A computed column is missing its name or expression.",
                            PipelineErrorType.Invalid);
                    additions.Add($"({text}) AS {Q(name!)}");
                }
                break;

            default:
                return SqlBuildResult.Fail("This step has no columns to add.", PipelineErrorType.Invalid);
        }

        if (additions.Count == 0)
            return SqlBuildResult.Fail("This step has no columns to add.", PipelineErrorType.Invalid);

        return SqlBuildResult.Ok($"SELECT *, {string.Join(", ", additions)} FROM {Q(input.Relation)}");
    }

    // ----------------------------------------------------------------- join

    private static SqlBuildResult Join(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!inputs.TryGetValue(PipelinePorts.Left, out var left))
            return SqlBuildResult.Fail("This join has nothing connected to its left input.", PipelineErrorType.Invalid);
        if (!inputs.TryGetValue(PipelinePorts.Right, out var right))
            return SqlBuildResult.Fail("This join has nothing connected to its right input.", PipelineErrorType.Invalid);

        var kind = (Str(node.Config, "kind") ?? "left").ToLowerInvariant();
        if (kind is not ("left" or "inner" or "anti"))
            return SqlBuildResult.Fail($"'{kind}' is not a supported join type.", PipelineErrorType.Invalid);

        if (node.Config?["on"] is not JsonArray pairs || pairs.Count == 0)
            return SqlBuildResult.Fail("This join has no columns to match on.", PipelineErrorType.Invalid);

        var conditions = new List<string>();
        string? firstRightKey = null;

        foreach (var pair in pairs)
        {
            var leftName = Str(pair as JsonObject, "left");
            var rightName = Str(pair as JsonObject, "right");

            if (string.IsNullOrWhiteSpace(leftName) || string.IsNullOrWhiteSpace(rightName))
                return SqlBuildResult.Fail("A match pair is missing one of its columns.", PipelineErrorType.Invalid);

            if (!left.ColumnLookup.TryGetValue(leftName!, out var leftActual))
                return SqlBuildResult.Fail(
                    $"Column '{leftName}' does not exist on the left input. Available: " +
                    $"{string.Join(", ", left.Columns.Select(c => c.Name))}.", PipelineErrorType.SchemaDrift);

            if (!right.ColumnLookup.TryGetValue(rightName!, out var rightActual))
                return SqlBuildResult.Fail(
                    $"Column '{rightName}' does not exist on the right input. Available: " +
                    $"{string.Join(", ", right.Columns.Select(c => c.Name))}.", PipelineErrorType.SchemaDrift);

            conditions.Add($"l.{Q(leftActual)} = r.{Q(rightActual)}");
            firstRightKey ??= rightActual;
        }

        var projections = new List<string> { "l.*" };

        if (kind != "anti")
        {
            if (node.Config?["bring"] is JsonObject bring && bring.Count > 0)
            {
                foreach (var (rightName, outNode) in bring)
                {
                    if (!right.ColumnLookup.TryGetValue(rightName, out var actual))
                        return SqlBuildResult.Fail(
                            $"Column '{rightName}' does not exist on the right input. Available: " +
                            $"{string.Join(", ", right.Columns.Select(c => c.Name))}.", PipelineErrorType.SchemaDrift);

                    var outName = (outNode as JsonValue)?.GetValue<string>();
                    projections.Add($"r.{Q(actual)} AS {Q(string.IsNullOrWhiteSpace(outName) ? actual : outName!)}");
                }
            }
            else
            {
                // Bring everything, suffixing only the names that would collide — suffixing all of them
                // would rename columns the author never asked about.
                var suffix = Str(node.Config, "suffix");
                if (string.IsNullOrEmpty(suffix)) suffix = "_r";

                var leftNames = new HashSet<string>(left.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var column in right.Columns)
                {
                    var target = leftNames.Contains(column.Name) ? column.Name + suffix : column.Name;
                    projections.Add($"r.{Q(column.Name)} AS {Q(target)}");
                }
            }
        }

        var joinType = kind == "inner" ? "INNER JOIN" : "LEFT JOIN";
        var sql = new StringBuilder()
            .Append("SELECT ").Append(string.Join(", ", projections))
            .Append(" FROM ").Append(Q(left.Relation)).Append(" l ")
            .Append(joinType).Append(' ').Append(Q(right.Relation)).Append(" r ON ")
            .Append(string.Join(" AND ", conditions));

        // An anti join is a left join keeping only the rows that found no partner.
        if (kind == "anti") sql.Append(" WHERE r.").Append(Q(firstRightKey!)).Append(" IS NULL");

        return SqlBuildResult.Ok(sql.ToString());
    }

    // ---------------------------------------------------------------- union

    private static SqlBuildResult Union(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        // The engine hands a multi-input step its inputs as in, in2, in3, … in connection order.
        var relations = inputs
            .Where(kv => kv.Key.StartsWith(PipelinePorts.In, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .ToList();

        if (relations.Count == 0)
            return SqlBuildResult.Fail("This step has no inputs.", PipelineErrorType.Invalid);

        if (relations.Count == 1)
            return SqlBuildResult.Ok($"SELECT * FROM {Q(relations[0].Relation)}");

        var distinct = string.Equals(Str(node.Config, "mode"), "distinct", StringComparison.OrdinalIgnoreCase);
        var byName = Bool(node.Config, "byName");

        // BY NAME lines columns up by name and nulls the gaps; without it DuckDB matches by position and
        // every input must have the same shape.
        var op = (distinct ? "UNION" : "UNION ALL") + (byName ? " BY NAME" : string.Empty);
        var parts = relations.Select(r => $"SELECT * FROM {Q(r.Relation)}");

        return SqlBuildResult.Ok(string.Join($" {op} ", parts));
    }

    // --------------------------------------------------------------- dedupe

    private static SqlBuildResult Dedupe(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var keys = StringList(node.Config, "keys");
        if (keys.Count == 0)
            return SqlBuildResult.Fail("This step has no key columns.", PipelineErrorType.Invalid);

        var resolved = new List<string>();
        foreach (var key in keys)
        {
            if (!input.ColumnLookup.TryGetValue(key, out var actual))
                return SqlBuildResult.Fail(
                    $"Column '{key}' does not exist in the incoming data. Available: " +
                    $"{string.Join(", ", input.Columns.Select(c => c.Name))}.", PipelineErrorType.SchemaDrift);
            resolved.Add(actual);
        }

        var partition = string.Join(", ", resolved.Select(Q));
        var orderBy = Str(node.Config, "orderBy");
        var order = string.Empty;

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            if (!input.ColumnLookup.TryGetValue(orderBy!, out var orderActual))
                return SqlBuildResult.Fail(
                    $"Order column '{orderBy}' does not exist in the incoming data. Available: " +
                    $"{string.Join(", ", input.Columns.Select(c => c.Name))}.", PipelineErrorType.SchemaDrift);

            var keep = (Str(node.Config, "keep") ?? "first").ToLowerInvariant();
            order = $" ORDER BY {Q(orderActual)} {(keep == "last" ? "DESC" : "ASC")}";
        }

        // A generated helper column, named so it cannot collide with anything a source would produce.
        const string RowNumber = "_pipe_rn";

        return SqlBuildResult.Ok(
            $"SELECT * EXCLUDE ({Q(RowNumber)}) FROM (" +
            $"SELECT *, ROW_NUMBER() OVER (PARTITION BY {partition}{order}) AS {Q(RowNumber)} " +
            $"FROM {Q(input.Relation)}) WHERE {Q(RowNumber)} = 1");
    }

    // ------------------------------------------------------------ aggregate

    private static SqlBuildResult Aggregate(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var groupBy = StringList(node.Config, "groupBy");
        if (node.Config?["metrics"] is not JsonArray metrics || metrics.Count == 0)
            return SqlBuildResult.Fail("This step has no summaries.", PipelineErrorType.Invalid);

        var projections = new List<string>();
        var groups = new List<string>();

        foreach (var name in groupBy)
        {
            if (!input.ColumnLookup.TryGetValue(name, out var actual))
                return SqlBuildResult.Fail(
                    $"Group column '{name}' does not exist in the incoming data. Available: " +
                    $"{string.Join(", ", input.Columns.Select(c => c.Name))}.", PipelineErrorType.SchemaDrift);

            projections.Add(Q(actual));
            groups.Add(Q(actual));
        }

        foreach (var metric in metrics)
        {
            var row = metric as JsonObject;
            var outName = Str(row, "name");
            var function = (Str(row, "function") ?? string.Empty).ToLowerInvariant();
            var column = Str(row, "column");

            if (string.IsNullOrWhiteSpace(outName))
                return SqlBuildResult.Fail("A summary is missing its output column name.", PipelineErrorType.Invalid);

            if (!PipelineAggregateFunctions.All.Contains(function))
                return SqlBuildResult.Fail(
                    $"'{function}' is not a supported summary function. Allowed: " +
                    $"{string.Join(", ", PipelineAggregateFunctions.All)}.", PipelineErrorType.Invalid);

            // count is the only one that works without a column, as count(*).
            string argument;
            if (function == PipelineAggregateFunctions.Count && (string.IsNullOrWhiteSpace(column) || column == "*"))
            {
                argument = "*";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(column))
                    return SqlBuildResult.Fail($"Summary '{outName}' needs a column.", PipelineErrorType.Invalid);

                if (!input.ColumnLookup.TryGetValue(column!, out var actual))
                    return SqlBuildResult.Fail(
                        $"Column '{column}' does not exist in the incoming data. Available: " +
                        $"{string.Join(", ", input.Columns.Select(c => c.Name))}.", PipelineErrorType.SchemaDrift);

                argument = Q(actual);
            }

            var call = function switch
            {
                PipelineAggregateFunctions.Sum => $"SUM({argument})",
                PipelineAggregateFunctions.Count => $"COUNT({argument})",
                PipelineAggregateFunctions.CountDistinct => $"COUNT(DISTINCT {argument})",
                PipelineAggregateFunctions.Avg => $"AVG({argument})",
                PipelineAggregateFunctions.Min => $"MIN({argument})",
                PipelineAggregateFunctions.Max => $"MAX({argument})",
                _ => null
            };

            if (call is null)
                return SqlBuildResult.Fail($"'{function}' is not a supported summary function.", PipelineErrorType.Invalid);

            projections.Add($"{call} AS {Q(outName!)}");
        }

        var sql = $"SELECT {string.Join(", ", projections)} FROM {Q(input.Relation)}";
        if (groups.Count > 0) sql += $" GROUP BY {string.Join(", ", groups)}";

        return SqlBuildResult.Ok(sql);
    }

    // ------------------------------------------------------------- raw SQL

    private static SqlBuildResult RawSql(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        var sql = Str(node.Config, "sql");
        if (string.IsNullOrWhiteSpace(sql))
            return SqlBuildResult.Fail("This step has no query.", PipelineErrorType.Invalid);

        // The author refers to inputs by node id, and a node's relation IS its id, so nothing needs
        // rewriting. Guarded here as well as at save time, because a stored graph is just text.
        if (!SelectOnlyGuard.IsSafeSelect(sql, out var error))
            return SqlBuildResult.Fail(error ?? "Only a single read-only SELECT is allowed.", PipelineErrorType.Invalid);

        return SqlBuildResult.Ok(sql!);
    }

    // -------------------------------------------------------------- helpers

    private static bool Single(
        IReadOnlyDictionary<string, RelationInput> inputs, out RelationInput input, out SqlBuildResult fail)
    {
        if (inputs.TryGetValue(PipelinePorts.In, out var found))
        {
            input = found;
            fail = default!;
            return true;
        }

        input = default!;
        fail = SqlBuildResult.Fail("This step has no input.", PipelineErrorType.Invalid);
        return false;
    }

    private static string? Str(JsonObject? config, string key)
    {
        var value = config?[key];
        return value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    private static bool Bool(JsonObject? config, string key)
    {
        var value = config?[key];
        return value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private static List<string> StringList(JsonObject? config, string key)
    {
        var result = new List<string>();
        if (config?[key] is not JsonArray array) return result;

        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                result.Add(s);
        }
        return result;
    }
}

/// <summary>One resolved input to a transform: the relation to read, and what columns it has.</summary>
public sealed class RelationInput
{
    public required string Relation { get; init; }
    public required IReadOnlyList<PipelineColumn> Columns { get; init; }

    private Dictionary<string, string>? _lookup;

    /// <summary>
    /// Case-insensitive column name -&gt; the column's actual casing. Authors type <c>customer_id</c> when
    /// the source produced <c>Customer_ID</c>; resolving through this means the generated SQL uses the real
    /// name while the config stays readable.
    /// </summary>
    public IReadOnlyDictionary<string, string> ColumnLookup =>
        _lookup ??= Columns
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Either SQL, or why it could not be built.</summary>
public sealed record SqlBuildResult(string? Sql, string? Error, string? ErrorType)
{
    public bool Success => Sql is not null;

    public static SqlBuildResult Ok(string sql) => new(sql, null, null);

    public static SqlBuildResult Fail(string error, string errorType) => new(null, error, errorType);
}

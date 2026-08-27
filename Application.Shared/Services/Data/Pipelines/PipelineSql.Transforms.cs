using System.Globalization;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The reshaping steps added in round two. Every one of these was already expressible in a
/// <c>transform.sql</c> step — DuckDB has had window functions, PIVOT and unnest all along — so what these
/// add is not capability but authorability: a form instead of a query, with column names validated against
/// the incoming schema so a typo is a message rather than a SQL error.
/// <para>
/// <b>Hard constraint on every builder here.</b> <c>MaterializeAsync</c> runs the output through
/// <see cref="SelectOnlyGuard"/> (first word must be SELECT or WITH) and then wraps it in
/// <c>CREATE OR REPLACE TABLE … AS …</c>. So PIVOT and UNPIVOT must be emitted as
/// <c>SELECT * FROM (PIVOT …)</c>; a bare PIVOT statement is rejected by the guard and would not survive the
/// wrapper either. Verified against DuckDB 1.3 before this was written.
/// </para>
/// </summary>
public static partial class PipelineSql
{
    // Helper column names are prefixed so they cannot collide with anything a real source produces.
    private const string HelperPrefix = "_pipe_";

    /// <summary>
    /// Window and aggregate functions the window step may use.
    /// <para>
    /// A whitelist, not a passthrough: the function name is interpolated into SQL, and this is the only
    /// thing standing between a dropdown and arbitrary expression injection. <c>count_distinct</c> is
    /// deliberately absent — DuckDB does not accept DISTINCT in a window aggregate, so offering it would
    /// produce a SQL error at run time rather than a validation message.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> WindowFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sum"] = "sum", ["avg"] = "avg", ["min"] = "min", ["max"] = "max", ["count"] = "count",
            ["row_number"] = "row_number", ["rank"] = "rank", ["dense_rank"] = "dense_rank",
            ["lag"] = "lag", ["lead"] = "lead",
            ["first_value"] = "first_value", ["last_value"] = "last_value"
        };

    /// <summary>Functions taking no column argument, so a missing column is not an error for them.</summary>
    private static readonly HashSet<string> RankingFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "row_number", "rank", "dense_rank" };

    private static readonly Dictionary<string, string> TextOperations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["trim"] = "trim", ["ltrim"] = "ltrim", ["rtrim"] = "rtrim",
            ["upper"] = "upper", ["lower"] = "lower", ["initcap"] = "initcap"
        };

    // ================================================================ sort

    private static SqlBuildResult Sort(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var terms = new List<string>();

        foreach (var row in Rows(node.Config, "columns"))
        {
            var column = Str(row, "column");
            if (string.IsNullOrWhiteSpace(column)) continue;

            if (!Resolve(input, column!, out var actual, out var missing)) return missing;

            var descending = string.Equals(Str(row, "direction"), "desc", StringComparison.OrdinalIgnoreCase);
            var nulls = (Str(row, "nulls") ?? string.Empty).ToLowerInvariant();

            var term = $"{Q(actual)} {(descending ? "DESC" : "ASC")}";
            if (nulls == "first") term += " NULLS FIRST";
            else if (nulls == "last") term += " NULLS LAST";

            terms.Add(term);
        }

        if (terms.Count == 0)
            return SqlBuildResult.Fail("This step has no sort columns.", PipelineErrorType.Invalid);

        return SqlBuildResult.Ok($"SELECT * FROM {Q(input.Relation)} ORDER BY {string.Join(", ", terms)}");
    }

    // ================================================================ rank

    private static SqlBuildResult Rank(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var output = Str(node.Config, "column");
        if (string.IsNullOrWhiteSpace(output))
            return SqlBuildResult.Fail("This step has no output column name.", PipelineErrorType.Invalid);

        var method = (Str(node.Config, "method") ?? "rank").ToLowerInvariant();
        if (!RankingFunctions.Contains(method))
            return SqlBuildResult.Fail(
                $"'{method}' is not a ranking method. Use row_number, rank or dense_rank.",
                PipelineErrorType.Invalid);

        var order = OrderClause(node.Config, "orderBy", input, out var orderFail);
        if (orderFail is not null) return orderFail;

        if (string.IsNullOrEmpty(order))
            return SqlBuildResult.Fail(
                "A rank needs at least one order column — without one the ranking would be arbitrary.",
                PipelineErrorType.Invalid);

        var partition = PartitionClause(node.Config, "partitionBy", input, out var partitionFail);
        if (partitionFail is not null) return partitionFail;

        var over = string.Join(" ", new[] { partition, order }.Where(x => !string.IsNullOrEmpty(x)));

        return SqlBuildResult.Ok(
            $"SELECT *, {method}() OVER ({over}) AS {Q(output!)} FROM {Q(input.Relation)}");
    }

    // ================================================================ surrogate key

    private static SqlBuildResult SurrogateKey(
        PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var output = Str(node.Config, "column");
        if (string.IsNullOrWhiteSpace(output))
            return SqlBuildResult.Fail("This step has no output column name.", PipelineErrorType.Invalid);

        var startAt = Number(node.Config, "startAt") ?? 1;

        // An optional order makes the numbering reproducible. Without one DuckDB is free to number rows in
        // whatever order it read them, which differs between runs — worth saying, since a surrogate key that
        // changes on re-run is not much of a key.
        var order = OrderClause(node.Config, "orderBy", input, out var orderFail);
        if (orderFail is not null) return orderFail;

        var offset = startAt == 1 ? string.Empty : $" + {(startAt - 1).ToString(CultureInfo.InvariantCulture)}";

        return SqlBuildResult.Ok(
            $"SELECT *, (row_number() OVER ({order}){offset}) AS {Q(output!)} FROM {Q(input.Relation)}");
    }

    // ================================================================ window

    private static SqlBuildResult Window(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var partition = PartitionClause(node.Config, "partitionBy", input, out var partitionFail);
        if (partitionFail is not null) return partitionFail;

        var order = OrderClause(node.Config, "orderBy", input, out var orderFail);
        if (orderFail is not null) return orderFail;

        // A running total is a frame, not a function: sum() OVER (ORDER BY x) already accumulates in
        // DuckDB, but saying so explicitly is what makes the difference from a partition-wide total
        // visible in the generated SQL.
        var cumulative = Bool(node.Config, "cumulative");
        var frame = cumulative && !string.IsNullOrEmpty(order)
            ? " ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW"
            : string.Empty;

        var additions = new List<string>();

        foreach (var row in Rows(node.Config, "columns"))
        {
            var name = Str(row, "name");
            var function = Str(row, "function");

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(function))
                return SqlBuildResult.Fail(
                    "A window column is missing its name or function.", PipelineErrorType.Invalid);

            if (!WindowFunctions.TryGetValue(function!, out var sqlFunction))
                return SqlBuildResult.Fail(
                    $"'{function}' is not an available window function. Available: "
                    + $"{string.Join(", ", WindowFunctions.Keys.OrderBy(k => k))}.",
                    PipelineErrorType.Invalid);

            string argument;

            if (RankingFunctions.Contains(sqlFunction))
            {
                argument = string.Empty;
            }
            else
            {
                var column = Str(row, "column");
                if (string.IsNullOrWhiteSpace(column))
                    return SqlBuildResult.Fail(
                        $"'{name}' uses {function}, which needs a column.", PipelineErrorType.Invalid);

                if (!Resolve(input, column!, out var actual, out var missing)) return missing;
                argument = Q(actual);
            }

            var over = string.Join(" ", new[] { partition, order }.Where(x => !string.IsNullOrEmpty(x)));
            additions.Add($"{sqlFunction}({argument}) OVER ({over}{frame}) AS {Q(name!)}");
        }

        if (additions.Count == 0)
            return SqlBuildResult.Fail("This step has no window columns.", PipelineErrorType.Invalid);

        return SqlBuildResult.Ok(
            $"SELECT *, {string.Join(", ", additions)} FROM {Q(input.Relation)}");
    }

    // ================================================================ fill

    private static SqlBuildResult Fill(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var columns = StringList(node.Config, "columns");
        if (columns.Count == 0)
            return SqlBuildResult.Fail("This step has no columns to fill.", PipelineErrorType.Invalid);

        var up = string.Equals(Str(node.Config, "direction"), "up", StringComparison.OrdinalIgnoreCase);

        var partition = PartitionClause(node.Config, "partitionBy", input, out var partitionFail);
        if (partitionFail is not null) return partitionFail;

        var order = OrderClause(node.Config, "orderBy", input, out var orderFail);
        if (orderFail is not null) return orderFail;

        if (string.IsNullOrEmpty(order))
            return SqlBuildResult.Fail(
                "Filling needs an order column: \"the previous value\" has no meaning without a row order.",
                PipelineErrorType.Invalid);

        var over = string.Join(" ", new[] { partition, order }.Where(x => !string.IsNullOrEmpty(x)));

        // IGNORE NULLS is the whole mechanism — it makes last_value reach back past the gap.
        var frame = up
            ? "ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING"
            : "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";
        var function = up ? "first_value" : "last_value";

        var replacements = new List<string>();

        foreach (var column in columns)
        {
            if (!Resolve(input, column, out var actual, out var missing)) return missing;

            replacements.Add(
                $"{function}({Q(actual)} IGNORE NULLS) OVER ({over} {frame}) AS {Q(actual)}");
        }

        // REPLACE keeps the column in its original position and type rather than appending a copy —
        // a filled column should look like the same column, not a new one.
        return SqlBuildResult.Ok(
            $"SELECT * REPLACE ({string.Join(", ", replacements)}) FROM {Q(input.Relation)}");
    }

    // ================================================================ text

    private static SqlBuildResult Text(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var replacements = new List<string>();
        var additions = new List<string>();

        foreach (var row in Rows(node.Config, "operations"))
        {
            var column = Str(row, "column");
            var operation = (Str(row, "operation") ?? string.Empty).ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(column))
                return SqlBuildResult.Fail("A text operation has no column.", PipelineErrorType.Invalid);

            if (!Resolve(input, column!, out var actual, out var missing)) return missing;

            string expression;

            if (TextOperations.TryGetValue(operation, out var simple))
            {
                expression = $"{simple}({Q(actual)})";
            }
            else if (operation is "replace" or "regexp_replace")
            {
                var find = Str(row, "find");
                if (string.IsNullOrEmpty(find))
                    return SqlBuildResult.Fail(
                        $"The {operation} on '{column}' has nothing to find.", PipelineErrorType.Invalid);

                // Literals, not interpolation: a find/replace value is free text from a form and would
                // otherwise be a way to inject SQL.
                expression = $"{operation}({Q(actual)}, {Lit(find!)}, {Lit(Str(row, "replace") ?? string.Empty)})";
            }
            else
            {
                return SqlBuildResult.Fail(
                    $"'{operation}' is not an available text operation. Available: "
                    + $"{string.Join(", ", TextOperations.Keys.OrderBy(k => k))}, replace, regexp_replace.",
                    PipelineErrorType.Invalid);
            }

            var target = Str(row, "into");

            if (string.IsNullOrWhiteSpace(target) || string.Equals(target, actual, StringComparison.OrdinalIgnoreCase))
                replacements.Add($"{expression} AS {Q(actual)}");
            else
                additions.Add($"{expression} AS {Q(target!)}");
        }

        if (replacements.Count == 0 && additions.Count == 0)
            return SqlBuildResult.Fail("This step has no operations.", PipelineErrorType.Invalid);

        var projection = "*";
        if (replacements.Count > 0) projection += $" REPLACE ({string.Join(", ", replacements)})";

        var extra = additions.Count > 0 ? ", " + string.Join(", ", additions) : string.Empty;

        return SqlBuildResult.Ok($"SELECT {projection}{extra} FROM {Q(input.Relation)}");
    }

    // ================================================================ split

    private static SqlBuildResult Split(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var column = Str(node.Config, "column");
        if (string.IsNullOrWhiteSpace(column))
            return SqlBuildResult.Fail("This step has no column to split.", PipelineErrorType.Invalid);

        if (!Resolve(input, column!, out var actual, out var missing)) return missing;

        var delimiter = Str(node.Config, "delimiter");
        if (string.IsNullOrEmpty(delimiter))
            return SqlBuildResult.Fail("This step has no delimiter.", PipelineErrorType.Invalid);

        var intoRows = string.Equals(Str(node.Config, "mode"), "rows", StringComparison.OrdinalIgnoreCase);

        if (intoRows)
        {
            var output = Str(node.Config, "into") ?? actual;

            // One row per part. The source column is excluded, because keeping both the whole string and
            // its parts on every row is almost never what was meant.
            return SqlBuildResult.Ok(
                $"SELECT * EXCLUDE ({Q(actual)}), unnest(str_split({Q(actual)}, {Lit(delimiter!)})) "
                + $"AS {Q(output)} FROM {Q(input.Relation)}");
        }

        var names = StringList(node.Config, "columns");
        if (names.Count == 0)
            return SqlBuildResult.Fail(
                "Splitting into columns needs the new column names — the number of them is what decides "
                + "how many parts to take.", PipelineErrorType.Invalid);

        var parts = names
            .Select((name, index) =>
                $"split_part({Q(actual)}, {Lit(delimiter!)}, {index + 1}) AS {Q(name)}")
            .ToList();

        return SqlBuildResult.Ok($"SELECT *, {string.Join(", ", parts)} FROM {Q(input.Relation)}");
    }

    // ================================================================ flatten

    private static SqlBuildResult Flatten(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var column = Str(node.Config, "column");
        if (string.IsNullOrWhiteSpace(column))
            return SqlBuildResult.Fail("This step has no column to flatten.", PipelineErrorType.Invalid);

        if (!Resolve(input, column!, out var actual, out var missing)) return missing;

        var output = Str(node.Config, "into") ?? actual;

        // The API source stores arrays as JSON TEXT (it has to — a column's type cannot depend on the row),
        // so text is the default and a real LIST column is the special case.
        var isList = (input.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, actual, StringComparison.OrdinalIgnoreCase))?.Type ?? string.Empty)
            .Contains("[]", StringComparison.Ordinal);

        var expression = isList
            ? $"unnest({Q(actual)})"
            : $"unnest(from_json({Q(actual)}, '[\"JSON\"]'))";

        return SqlBuildResult.Ok(
            $"SELECT * EXCLUDE ({Q(actual)}), {expression} AS {Q(output)} FROM {Q(input.Relation)}");
    }

    // ================================================================ parse

    private static SqlBuildResult Parse(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var column = Str(node.Config, "column");
        if (string.IsNullOrWhiteSpace(column))
            return SqlBuildResult.Fail("This step has no column to parse.", PipelineErrorType.Invalid);

        if (!Resolve(input, column!, out var actual, out var missing)) return missing;

        if (node.Config?["fields"] is not JsonObject fields || fields.Count == 0)
            return SqlBuildResult.Fail(
                "This step has no fields to pull out of the JSON.", PipelineErrorType.Invalid);

        var additions = new List<string>();

        foreach (var (name, path) in fields)
        {
            var text = (path as JsonValue)?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return SqlBuildResult.Fail($"'{name}' has no JSON path.", PipelineErrorType.Invalid);

            // A bare name is taken as a top-level property, so the common case needs no $ prefix.
            var jsonPath = text!.StartsWith('$') ? text : "$." + text;

            additions.Add($"json_extract_string({Q(actual)}, {Lit(jsonPath)}) AS {Q(name)}");
        }

        var drop = Bool(node.Config, "dropSource")
            ? $" EXCLUDE ({Q(actual)})"
            : string.Empty;

        return SqlBuildResult.Ok(
            $"SELECT *{drop}, {string.Join(", ", additions)} FROM {Q(input.Relation)}");
    }

    // ================================================================ pivot

    private static SqlBuildResult Pivot(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var on = Str(node.Config, "on");
        if (string.IsNullOrWhiteSpace(on))
            return SqlBuildResult.Fail(
                "This step has no column to turn into columns.", PipelineErrorType.Invalid);
        if (!Resolve(input, on!, out var onActual, out var onMissing)) return onMissing;

        var function = (Str(node.Config, "function") ?? "sum").ToLowerInvariant();
        if (!PipelineAggregateFunctions.All.Contains(function) || function == "count_distinct")
            return SqlBuildResult.Fail(
                $"'{function}' cannot be used here. Available: sum, count, avg, min, max.",
                PipelineErrorType.Invalid);

        var valueColumn = Str(node.Config, "value");
        string aggregate;

        if (function == "count" && string.IsNullOrWhiteSpace(valueColumn))
        {
            aggregate = "count(*)";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(valueColumn))
                return SqlBuildResult.Fail($"{function} needs a value column.", PipelineErrorType.Invalid);
            if (!Resolve(input, valueColumn!, out var valueActual, out var valueMissing)) return valueMissing;
            aggregate = $"{function}({Q(valueActual)})";
        }

        var groupBy = new List<string>();
        foreach (var column in StringList(node.Config, "groupBy"))
        {
            if (!Resolve(input, column, out var actual, out var missing)) return missing;
            groupBy.Add(Q(actual));
        }

        var group = groupBy.Count > 0 ? $" GROUP BY {string.Join(", ", groupBy)}" : string.Empty;

        // Wrapped in SELECT * FROM (...) — mandatory, not stylistic. See the note on this class.
        return SqlBuildResult.Ok(
            $"SELECT * FROM (PIVOT {Q(input.Relation)} ON {Q(onActual)} USING {aggregate}{group})");
    }

    // ================================================================ unpivot

    private static SqlBuildResult Unpivot(PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!Single(inputs, out var input, out var fail)) return fail;

        var columns = StringList(node.Config, "columns");
        if (columns.Count == 0)
            return SqlBuildResult.Fail(
                "This step has no columns to turn into rows.", PipelineErrorType.Invalid);

        var resolved = new List<string>();
        foreach (var column in columns)
        {
            if (!Resolve(input, column, out var actual, out var missing)) return missing;
            resolved.Add(Q(actual));
        }

        var nameColumn = Str(node.Config, "nameColumn") ?? "name";
        var valueColumn = Str(node.Config, "valueColumn") ?? "value";

        return SqlBuildResult.Ok(
            $"SELECT * FROM (UNPIVOT {Q(input.Relation)} ON {string.Join(", ", resolved)} "
            + $"INTO NAME {Q(nameColumn)} VALUE {Q(valueColumn)})");
    }

    // ================================================================ shared helpers

    /// <summary>
    /// Resolves a configured column name against the incoming schema, reporting drift the same way the
    /// existing steps do — naming the column AND what is actually available, because "column not found" on
    /// its own sends people to the wrong place.
    /// </summary>
    private static bool Resolve(
        RelationInput input, string column, out string actual, out SqlBuildResult failure)
    {
        if (input.ColumnLookup.TryGetValue(column, out var found))
        {
            actual = found;
            failure = default!;
            return true;
        }

        actual = string.Empty;
        failure = SqlBuildResult.Fail(
            $"Column '{column}' does not exist in the incoming data. Available: "
            + $"{string.Join(", ", input.Columns.Select(c => c.Name))}.",
            PipelineErrorType.SchemaDrift);
        return false;
    }

    /// <summary>PARTITION BY over a list field, or empty when there is none.</summary>
    private static string PartitionClause(
        JsonObject? config, string key, RelationInput input, out SqlBuildResult? failure)
    {
        failure = null;
        var columns = StringList(config, key);
        if (columns.Count == 0) return string.Empty;

        var resolved = new List<string>();
        foreach (var column in columns)
        {
            if (!Resolve(input, column, out var actual, out var missing))
            {
                failure = missing;
                return string.Empty;
            }
            resolved.Add(Q(actual));
        }

        return $"PARTITION BY {string.Join(", ", resolved)}";
    }

    /// <summary>
    /// ORDER BY over a list of either plain column names or <c>{column, direction}</c> rows, since the
    /// inspector's simple list and its grid produce different shapes for the same idea.
    /// </summary>
    private static string OrderClause(
        JsonObject? config, string key, RelationInput input, out SqlBuildResult? failure)
    {
        failure = null;
        var terms = new List<string>();

        if (config?[key] is JsonArray array)
        {
            foreach (var entry in array)
            {
                string? column;
                var descending = false;

                if (entry is JsonObject row)
                {
                    column = Str(row, "column");
                    descending = string.Equals(Str(row, "direction"), "desc", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    column = (entry as JsonValue)?.ToString();
                }

                if (string.IsNullOrWhiteSpace(column)) continue;

                if (!Resolve(input, column!, out var actual, out var missing))
                {
                    failure = missing;
                    return string.Empty;
                }

                terms.Add($"{Q(actual)}{(descending ? " DESC" : string.Empty)}");
            }
        }

        return terms.Count == 0 ? string.Empty : $"ORDER BY {string.Join(", ", terms)}";
    }

    /// <summary>Rows of a list-shaped config field, tolerating a single object for a one-row list.</summary>
    private static IEnumerable<JsonObject> Rows(JsonObject? config, string key) =>
        config?[key] switch
        {
            JsonArray array => array.OfType<JsonObject>(),
            JsonObject single => [single],
            _ => []
        };

    private static int? Number(JsonObject? config, string key)
    {
        var value = config?[key];
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : null;
    }
}

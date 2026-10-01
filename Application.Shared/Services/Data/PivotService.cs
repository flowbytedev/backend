using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;

namespace Application.Shared.Services.Data;

/// <summary>
/// Excel-style pivot over one dataset table: the aggregation runs in the engine (a GROUP BY over the row and
/// column fields), and the long result is reshaped into a cross-tab here, so the browser only receives cells.
/// </summary>
/// <remarks>
/// GROUP BY + reshape rather than the engine's own <c>PIVOT</c>: only DuckDB and SQL Server have one, their
/// syntaxes differ, and neither produces grouped column headers or totals. A GROUP BY is the same on every
/// source a dataset can be backed by, so the snapshot and the live path share one implementation.
/// <para>
/// Every identifier in the generated SQL is a column name checked against the table's real schema and then
/// quoted; every value is an escaped literal. No caller-written SQL is ever executed.
/// </para>
/// <para>
/// Access: this enforces <b>table</b> grants only (the controller checks them), exactly like the data
/// viewer's grid on the same screen, which already pages through every row and column of the table. It
/// does not apply per-user column masking or row-level security — see CLAUDE.md for where those are
/// enforced. Because the SQL is generated here rather than parsed, adding them later is a matter of
/// restricting the resolvable columns and appending the RLS predicates to the WHERE clause.
/// </para>
/// </remarks>
public interface IPivotService
{
    /// <summary>
    /// Builds the pivot. Problems with the layout or the query come back in <see cref="PivotResult.Error"/>,
    /// never as an exception. The caller has already checked the dataset and table are visible to the user.
    /// </summary>
    Task<PivotResult> RunAsync(Dataset dataset, string companyId, string tableName, PivotRequest request,
        CancellationToken ct = default);
}

public class PivotService : IPivotService
{
    private const int MaxRowFields = 6;
    private const int MaxColumnFields = 4;
    private const int MaxValueFields = 10;
    private const int MaxFilters = 20;

    // Long-format (grouped) rows the main query may return. The live cap is DatabaseTableService's own
    // external row ceiling, which ExecuteQueryAsync clamps to regardless of what is asked for.
    private const int MaxSnapshotGroups = 100_000;
    private const int MaxLiveGroups = 5_000;

    // Distinct column-field combinations. Each becomes (values × 1) grid columns, so this bounds the width.
    private const int MaxColumnKeys = 250;

    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(120);

    private const string BlankLabel = "(blank)";
    private const string KeySeparator = "\u001F";

    private readonly IDuckdbService _duckdb;
    private readonly IDatabaseTableService _dbTables;

    public PivotService(IDuckdbService duckdb, IDatabaseTableService dbTables)
    {
        _duckdb = duckdb;
        _dbTables = dbTables;
    }

    public async Task<PivotResult> RunAsync(Dataset dataset, string companyId, string tableName,
        PivotRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new PivotResult();
        request ??= new PivotRequest();

        var rowFields = request.Rows ?? new List<PivotField>();
        var colFields = request.Columns ?? new List<PivotField>();
        var valueFields = (request.Values ?? new List<PivotValueField>()).ToList();
        var filters = request.Filters ?? new List<FilterCondition>();

        if (rowFields.Count > MaxRowFields)
            return Fail(result, $"Use at most {MaxRowFields} row fields.");
        if (colFields.Count > MaxColumnFields)
            return Fail(result, $"Use at most {MaxColumnFields} column fields.");
        if (valueFields.Count > MaxValueFields)
            return Fail(result, $"Use at most {MaxValueFields} value fields.");
        if (filters.Count > MaxFilters)
            return Fail(result, $"Use at most {MaxFilters} filters.");

        // Nothing to aggregate is still a useful layout — Excel shows the distinct labels — so it counts rows.
        if (valueFields.Count == 0)
            valueFields.Add(new PivotValueField { Column = PivotValueField.AllRows, Aggregation = PivotAggregations.Count });

        // ---- where the data lives, and in which dialect ----
        var live = request.LiveSource;
        if (live && (dataset.SourceType != DatasetSourceType.External || string.IsNullOrWhiteSpace(dataset.SourceEntityId)))
            return Fail(result, "This dataset is not backed by an external database.");

        var dialect = DataSourceType.DuckDB;
        List<Column> schema;
        if (live)
        {
            var connection = await _dbTables.GetConnectionAsync(dataset.SourceEntityId!, companyId, ct);
            if (connection is null)
                return Fail(result, "No connection is configured for this database source.");
            dialect = connection.DatabaseType;

            var described = await _dbTables.GetTableSchemaAsync(dataset.SourceEntityId!, companyId, tableName, ct);
            if (!string.IsNullOrEmpty(described.Error))
                return Fail(result, $"The table's columns could not be read: {described.Error}");
            schema = described.Columns ?? new List<Column>();
        }
        else
        {
            try { schema = await _duckdb.GetTableColumnsAsync(dataset.Id!, tableName); }
            catch (Exception ex) { return Fail(result, $"The table's columns could not be read: {ex.Message}"); }
        }

        if (schema.Count == 0)
            return Fail(result, "The table's columns could not be read.");

        var columnsByName = schema
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var sql = new PivotSql(dialect, live
            ? ExternalConnectionFactory.QuoteQualified(dialect, tableName)
            : ExternalConnectionFactory.QuoteIdentifier(DataSourceType.DuckDB, tableName));

        // ---- resolve the layout against the real schema ----
        var rowExprs = new List<string>();
        var colExprs = new List<string>();
        foreach (var (fields, exprs) in new[] { (rowFields, rowExprs), (colFields, colExprs) })
        {
            foreach (var field in fields)
            {
                if (!columnsByName.TryGetValue(field?.Column ?? string.Empty, out var column))
                    return Fail(result, $"Column '{field?.Column}' is not in this table.");

                var part = string.IsNullOrWhiteSpace(field!.DatePart) ? null : field.DatePart.Trim().ToLowerInvariant();
                if (part is not null && !PivotDateParts.All.Contains(part))
                    return Fail(result, $"'{field.DatePart}' is not a date grouping. Use year, quarter, month or date.");

                field.Column = column.Name;
                field.DatePart = part;
                exprs.Add(sql.GroupExpr(column.Name, part));
            }
        }

        var aggExprs = new List<string>();
        foreach (var value in valueFields)
        {
            var aggregation = PivotAggregations.All.FirstOrDefault(a =>
                string.Equals(a, value?.Aggregation, StringComparison.OrdinalIgnoreCase));
            if (aggregation is null)
                return Fail(result, $"'{value?.Aggregation}' is not a supported aggregation.");

            if (value!.Column == PivotValueField.AllRows)
            {
                value.Aggregation = PivotAggregations.Count; // "*" only ever means a row count
                aggExprs.Add(sql.RowCount());
                result.ValueLabels.Add("Count of rows");
                continue;
            }

            if (!columnsByName.TryGetValue(value.Column ?? string.Empty, out var column))
                return Fail(result, $"Column '{value.Column}' is not in this table.");

            value.Column = column.Name;
            value.Aggregation = aggregation;
            aggExprs.Add(sql.AggExpr(column.Name, aggregation));
            result.ValueLabels.Add($"{PivotAggregations.Label(aggregation)} of {column.Name}");
        }

        var whereParts = new List<string>();
        foreach (var filter in filters)
        {
            if (filter is null) continue;
            if (!columnsByName.TryGetValue(filter.ColumnName ?? string.Empty, out var column))
                return Fail(result, $"Filter column '{filter.ColumnName}' is not in this table.");

            var clause = sql.FilterClause(column, filter.Operator, filter.Value, out var filterError);
            if (filterError is not null) return Fail(result, filterError);
            if (clause is not null) whereParts.Add(clause);
        }
        var where = whereParts.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", whereParts);

        // ---- run ----
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (live) cts.CancelAfter(LiveTimeout); // DuckDB applies its own query timeout
        var cap = live ? MaxLiveGroups : MaxSnapshotGroups;

        async Task<(SqlQueryResult Grid, string? Error)> Run(IReadOnlyList<string> groupExprs)
        {
            var statement = sql.Select(groupExprs, aggExprs, where);
            var grid = live
                ? await _dbTables.ExecuteQueryAsync(dataset.SourceEntityId!, companyId, statement, cap, cts.Token)
                : await _duckdb.ExecuteGeneratedReadAsync(dataset.Id!, statement, cap, cts.Token);

            if (cts.IsCancellationRequested && !ct.IsCancellationRequested)
                return (grid, $"The source took longer than {LiveTimeout.TotalSeconds:N0}s. Add a filter or use fewer fields.");
            if (!string.IsNullOrWhiteSpace(grid.Error))
                return (grid, grid.Error);
            if (grid.Truncated)
                return (grid, $"This layout produces more than {cap:N0} combinations. " +
                              "Add a filter, or remove a field from Rows or Columns.");
            return (grid, null);
        }

        var allGroups = rowExprs.Concat(colExprs).ToList();
        result.Sql = sql.Select(allGroups, aggExprs, where);

        var (main, mainError) = await Run(allGroups);
        if (mainError is not null) return Fail(result, mainError);

        var nr = rowFields.Count;
        var nc = colFields.Count;
        var nv = aggExprs.Count;

        // ---- reshape: long (one row per row×column combination) → cross-tab ----
        var columnKeys = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        var rowEntries = new Dictionary<string, RowEntry>(StringComparer.Ordinal);

        foreach (var record in main.Rows)
        {
            var groups = ReadGroups(record, 0, nr + nc);
            var rowRaw = groups[..nr];
            var colRaw = groups[nr..];

            var columnKey = Key(colRaw);
            if (!columnKeys.ContainsKey(columnKey))
            {
                columnKeys[columnKey] = colRaw;
                if (columnKeys.Count > MaxColumnKeys)
                    return Fail(result, $"The column fields produce more than {MaxColumnKeys} distinct columns. " +
                                        "Move a field to Rows, or add a filter.");
            }

            var rowKey = Key(rowRaw);
            if (!rowEntries.TryGetValue(rowKey, out var entry))
                rowEntries[rowKey] = entry = new RowEntry(rowRaw);
            entry.Cells[columnKey] = ReadAggregates(record, nv);
        }

        // Column order: by the raw values, so numbers and dates sort as such and "(blank)" goes last.
        var orderedColumnKeys = columnKeys.OrderBy(kv => kv.Value, TupleComparer.Instance).ToList();
        var columnIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var c = 0; c < orderedColumnKeys.Count; c++)
            columnIndex[orderedColumnKeys[c].Key] = c;

        // A layout with no column fields still has exactly one (empty) column key — unless the filters left
        // no rows at all, in which case the value columns are still worth showing as headers.
        if (orderedColumnKeys.Count == 0 && nc == 0)
        {
            orderedColumnKeys.Add(new KeyValuePair<string, object?[]>(Key(Array.Empty<object?>()), Array.Empty<object?>()));
            columnIndex[orderedColumnKeys[0].Key] = 0;
        }

        for (var c = 0; c < orderedColumnKeys.Count; c++)
        {
            var path = orderedColumnKeys[c].Value
                .Select((raw, i) => Label(raw, colFields[i].DatePart))
                .ToList();
            for (var v = 0; v < nv; v++)
                result.Columns.Add(new PivotColumn { Field = CellField(c, v), Path = path, ValueIndex = v });
        }

        for (var i = 0; i < nr; i++)
        {
            var part = PivotDateParts.Label(rowFields[i].DatePart);
            result.RowHeaders.Add(new PivotHeader
            {
                Field = "r" + i,
                Label = part.Length == 0 ? rowFields[i].Column : $"{rowFields[i].Column} ({part})"
            });
        }

        // ---- totals ----
        // Each total is its own aggregate query rather than a sum of cells: an average, a distinct count or
        // a min of the totals is not the average/distinct count/min of the cells.
        var showTotals = request.ShowTotals && (nr > 0 || nc > 0);
        Dictionary<string, object?[]>? rowTotals = null;   // row key → totals across all columns
        Dictionary<string, object?[]>? columnTotals = null; // column key → totals down all rows
        object?[]? grand = null;

        if (showTotals)
        {
            var (grandGrid, grandError) = await Run(Array.Empty<string>());
            if (grandError is not null) return Fail(result, grandError);
            grand = grandGrid.Rows.Count > 0 ? ReadAggregates(grandGrid.Rows[0], nv) : new object?[nv];

            if (nc > 0)
            {
                if (nr > 0)
                {
                    var (byRow, error) = await Run(rowExprs);
                    if (error is not null) return Fail(result, error);
                    rowTotals = byRow.Rows.ToDictionary(r => Key(ReadGroups(r, 0, nr)), r => ReadAggregates(r, nv), StringComparer.Ordinal);

                    var (byColumn, columnError) = await Run(colExprs);
                    if (columnError is not null) return Fail(result, columnError);
                    columnTotals = byColumn.Rows.ToDictionary(r => Key(ReadGroups(r, 0, nc)), r => ReadAggregates(r, nv), StringComparer.Ordinal);
                }
                else
                {
                    // No row fields: the one row's total across every column is the grand total.
                    rowTotals = new Dictionary<string, object?[]>(StringComparer.Ordinal) { [Key(Array.Empty<object?>())] = grand };
                }

                for (var v = 0; v < nv; v++)
                    result.Columns.Add(new PivotColumn { Field = TotalField(v), Path = new List<string> { "Total" }, ValueIndex = v, IsTotal = true });
            }
        }

        // ---- rows ----
        // Per-field sort rank, so the grid can re-sort a row field by its underlying value, not its label.
        var ranks = new List<Dictionary<string, int>>();
        for (var i = 0; i < nr; i++)
        {
            var field = i;
            var ordered = rowEntries.Values
                .Select(e => e.Raw[field])
                .GroupBy(Token, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(v => v, ValueComparer.Instance)
                .ToList();
            ranks.Add(ordered.Select((v, idx) => (Token(v), idx)).ToDictionary(x => x.Item1, x => x.idx, StringComparer.Ordinal));
        }

        foreach (var (rowKey, entry) in rowEntries.OrderBy(kv => kv.Value.Raw, TupleComparer.Instance))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < nr; i++)
            {
                row["r" + i] = Label(entry.Raw[i], rowFields[i].DatePart);
                row["__o" + i] = ranks[i][Token(entry.Raw[i])];
            }

            foreach (var (columnKey, cells) in entry.Cells)
            {
                var c = columnIndex[columnKey];
                for (var v = 0; v < nv; v++)
                    row[CellField(c, v)] = cells[v];
            }

            if (rowTotals is not null && rowTotals.TryGetValue(rowKey, out var totals))
                for (var v = 0; v < nv; v++)
                    row[TotalField(v)] = totals[v];

            result.Rows.Add(row);
        }

        if (showTotals && nr > 0 && grand is not null)
        {
            var totalRow = new Dictionary<string, object?>(StringComparer.Ordinal) { ["r0"] = "Grand total" };
            if (nc == 0)
            {
                for (var v = 0; v < nv; v++)
                    totalRow[CellField(0, v)] = grand[v];
            }
            else
            {
                foreach (var (columnKey, c) in columnIndex)
                    if (columnTotals is not null && columnTotals.TryGetValue(columnKey, out var totals))
                        for (var v = 0; v < nv; v++)
                            totalRow[CellField(c, v)] = totals[v];
                for (var v = 0; v < nv; v++)
                    totalRow[TotalField(v)] = grand[v];
            }
            result.GrandTotal = totalRow;
        }

        result.ColumnKeyCount = nc == 0 ? 0 : orderedColumnKeys.Count;
        stopwatch.Stop();
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    // ---- reading the grouped result ----

    private sealed class RowEntry
    {
        public RowEntry(object?[] raw) => Raw = raw;
        public object?[] Raw { get; }
        public Dictionary<string, object?[]> Cells { get; } = new(StringComparer.Ordinal);
    }

    private static string CellField(int columnKey, int value) => $"v{columnKey}_{value}";
    private static string TotalField(int value) => $"t_{value}";

    private static object?[] ReadGroups(Dictionary<string, object?> record, int start, int count)
    {
        var values = new object?[count];
        for (var i = 0; i < count; i++)
            values[i] = Get(record, PivotSql.GroupAlias(start + i));
        return values;
    }

    private static object?[] ReadAggregates(Dictionary<string, object?> record, int count)
    {
        var values = new object?[count];
        for (var i = 0; i < count; i++)
            values[i] = CellValue(Get(record, PivotSql.AggAlias(i)));
        return values;
    }

    private static object? Get(Dictionary<string, object?> record, string alias)
    {
        if (record.TryGetValue(alias, out var value)) return Unwrap(value);
        // Some providers fold the case of an alias even when it is quoted.
        foreach (var (name, v) in record)
            if (string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))
                return Unwrap(v);
        return null;
    }

    private static object? Unwrap(object? value) => value is DBNull ? null : value;

    /// <summary>An aggregate as it should reach the browser: numbers stay numbers; anything JSON cannot
    /// carry as a plain value becomes its label.</summary>
    private static object? CellValue(object? value) => value switch
    {
        null => null,
        double d when double.IsNaN(d) || double.IsInfinity(d) => null,
        float f when float.IsNaN(f) || float.IsInfinity(f) => null,
        System.Numerics.BigInteger big => big >= long.MinValue && big <= long.MaxValue ? (long)big : (double)big,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Guid => Label(value, null),
        byte[] => null,
        _ => value
    };

    // ---- keys, labels and ordering ----

    /// <summary>An identity for a raw value that is stable across the main and the total queries.</summary>
    private static string Token(object? value) => value switch
    {
        null => "\0",
        DateTime d => "D" + d.Ticks.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset o => "O" + o.UtcTicks.ToString(CultureInfo.InvariantCulture),
        IFormattable f => value.GetType().Name + ":" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => "S:" + value
    };

    private static string Key(object?[] values) => string.Join(KeySeparator, values.Select(Token));

    private static string Label(object? value, string? datePart)
    {
        if (value is null) return BlankLabel;

        if (datePart is PivotDateParts.Year or PivotDateParts.Quarter or PivotDateParts.Month
            && TryWhole(value, out var n))
        {
            return datePart switch
            {
                PivotDateParts.Quarter => "Q" + n,
                PivotDateParts.Month when n is >= 1 and <= 12 =>
                    CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName((int)n),
                _ => n.ToString(CultureInfo.InvariantCulture)
            };
        }

        return value switch
        {
            DateTime d => d.TimeOfDay == TimeSpan.Zero
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            // Distinct from NULL's "(blank)": they are different groups, and sharing a label would show
            // two indistinguishable headers.
            string s when s.Length == 0 => "(empty)",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? BlankLabel
        };
    }

    /// <summary>EXTRACT returns a numeric/double on some engines, so 2024 can arrive as 2024.0.</summary>
    private static bool TryWhole(object value, out long whole)
    {
        whole = 0;
        if (!IsNumber(value)) return false;
        var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (d != decimal.Truncate(d)) return false;
        whole = (long)d;
        return true;
    }

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint
        or long or ulong or float or double or decimal;

    /// <summary>Nulls last; numbers numerically; dates chronologically; everything else as text.</summary>
    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? a, object? b)
        {
            if (a is null || b is null) return a is null ? (b is null ? 0 : 1) : -1;

            if (IsNumber(a) && IsNumber(b))
            {
                try { return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture)); }
                catch (OverflowException) { return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture)); }
            }

            if (a is IComparable comparable && a.GetType() == b.GetType() && a is not string)
                return comparable.CompareTo(b);

            return StringComparer.CurrentCultureIgnoreCase.Compare(
                Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
        }
    }

    private sealed class TupleComparer : IComparer<object?[]>
    {
        public static readonly TupleComparer Instance = new();

        public int Compare(object?[]? a, object?[]? b)
        {
            a ??= Array.Empty<object?>();
            b ??= Array.Empty<object?>();
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = ValueComparer.Instance.Compare(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }

    private static PivotResult Fail(PivotResult result, string error)
    {
        result.Error = error;
        result.Rows.Clear();
        result.Columns.Clear();
        result.RowHeaders.Clear();
        result.GrandTotal = null;
        return result;
    }

    // ---- SQL generation ----

    /// <summary>Dialect-aware pieces of the aggregate statement. Identifiers are quoted, literals escaped.</summary>
    private sealed class PivotSql
    {
        private readonly DataSourceType _dialect;
        private readonly string _table;

        public PivotSql(DataSourceType dialect, string quotedTable)
        {
            _dialect = dialect;
            _table = quotedTable;
        }

        // Prefixed so they cannot collide with a real column: ClickHouse aliases are visible to the rest of
        // the statement, so an alias "a0" would shadow a column named a0 inside the GROUP BY.
        public static string GroupAlias(int i) => "__pv_g" + i;
        public static string AggAlias(int i) => "__pv_a" + i;

        private string Q(string identifier) => ExternalConnectionFactory.QuoteIdentifier(_dialect, identifier);

        public string Select(IReadOnlyList<string> groupExprs, IReadOnlyList<string> aggExprs, string where)
        {
            var select = groupExprs.Select((e, i) => $"{e} AS {Q(GroupAlias(i))}")
                .Concat(aggExprs.Select((e, i) => $"{e} AS {Q(AggAlias(i))}"));
            var statement = $"SELECT {string.Join(", ", select)} FROM {_table}{where}";
            // GROUP BY repeats the expressions rather than naming the aliases: SQL Server does not allow an
            // alias there.
            return groupExprs.Count == 0 ? statement : $"{statement} GROUP BY {string.Join(", ", groupExprs)}";
        }

        public string GroupExpr(string column, string? datePart)
        {
            var c = Q(column);
            return (datePart, _dialect) switch
            {
                (null, _) => c,
                (PivotDateParts.Year, DataSourceType.SQLServer) => $"DATEPART(year, {c})",
                (PivotDateParts.Quarter, DataSourceType.SQLServer) => $"DATEPART(quarter, {c})",
                (PivotDateParts.Month, DataSourceType.SQLServer) => $"DATEPART(month, {c})",
                (PivotDateParts.Date, DataSourceType.SQLServer) => $"CAST({c} AS date)",
                (PivotDateParts.Year, DataSourceType.ClickHouse) => $"toYear({c})",
                (PivotDateParts.Quarter, DataSourceType.ClickHouse) => $"toQuarter({c})",
                (PivotDateParts.Month, DataSourceType.ClickHouse) => $"toMonth({c})",
                (PivotDateParts.Date, DataSourceType.ClickHouse) => $"toDate({c})",
                (PivotDateParts.Year, _) => $"EXTRACT(YEAR FROM {c})",
                (PivotDateParts.Quarter, _) => $"EXTRACT(QUARTER FROM {c})",
                (PivotDateParts.Month, _) => $"EXTRACT(MONTH FROM {c})",
                (PivotDateParts.Date, _) => $"CAST({c} AS DATE)",
                _ => c
            };
        }

        // SQL Server's COUNT is int and overflows past ~2.1B rows; COUNT_BIG is bigint.
        public string RowCount() => _dialect == DataSourceType.SQLServer ? "COUNT_BIG(*)" : "COUNT(*)";

        public string AggExpr(string column, string aggregation)
        {
            var c = Q(column);
            return aggregation switch
            {
                PivotAggregations.Sum => $"SUM({c})",
                // SQL Server averages an int column in integer arithmetic (AVG of 1 and 2 is 1).
                PivotAggregations.Avg => _dialect == DataSourceType.SQLServer ? $"AVG(CAST({c} AS FLOAT))" : $"AVG({c})",
                PivotAggregations.Count => _dialect == DataSourceType.SQLServer ? $"COUNT_BIG({c})" : $"COUNT({c})",
                PivotAggregations.CountDistinct => $"COUNT(DISTINCT {c})",
                PivotAggregations.Min => $"MIN({c})",
                PivotAggregations.Max => $"MAX({c})",
                _ => throw new ArgumentOutOfRangeException(nameof(aggregation), aggregation, null)
            };
        }

        /// <summary>
        /// One filter condition, or null when it has no value yet (a half-filled filter chip). Numeric
        /// columns compared to numeric input get bare number literals, so strict engines do not reject a
        /// number-vs-string comparison; text matching is case-insensitive, as Excel's is.
        /// </summary>
        public string? FilterClause(Column column, string? op, string? value, out string? error)
        {
            error = null;
            var c = Q(column.Name);
            var kind = (op ?? "equals").Trim().ToLowerInvariant();

            switch (kind)
            {
                case "isblank": return $"{c} IS NULL";
                case "notblank": return $"{c} IS NOT NULL";
            }

            if (string.IsNullOrWhiteSpace(value)) return null;
            var numeric = IsNumericType(column.DataType);

            if (kind is "equals" or "notequals")
            {
                // ';' separates a list of values (IN), the same convention as metric filters.
                var items = value.Split(';').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
                if (items.Count == 0) return null;
                var literals = items.Select(v => Literal(v, numeric)).ToList();

                if (kind == "equals")
                    return literals.Count == 1 ? $"{c} = {literals[0]}" : $"{c} IN ({string.Join(", ", literals)})";

                // Excel's "does not equal" keeps blanks; SQL's <> would drop them.
                var test = literals.Count == 1 ? $"{c} <> {literals[0]}" : $"{c} NOT IN ({string.Join(", ", literals)})";
                return $"({test} OR {c} IS NULL)";
            }

            // Both execution paths refuse a statement containing ';' (it reads as a second statement).
            if (value.Contains(';'))
            {
                error = $"The filter on '{column.Name}' contains ';', which is only allowed to separate values for 'equals'.";
                return null;
            }

            switch (kind)
            {
                case "contains":
                case "notcontains":
                case "startswith":
                case "endswith":
                    var pattern = kind switch
                    {
                        "startswith" => value + "%",
                        "endswith" => "%" + value,
                        _ => "%" + value + "%"
                    };
                    var like = _dialect is DataSourceType.DuckDB or DataSourceType.PostgreSQL or DataSourceType.ClickHouse
                        ? "ILIKE" : "LIKE"; // SQL Server and MySQL default collations are already case-insensitive
                    var text = IsTextType(column.DataType) ? c : TextExpr(c);
                    return kind == "notcontains"
                        ? $"({text} NOT {like} {Quote(pattern)} OR {c} IS NULL)"
                        : $"{text} {like} {Quote(pattern)}";
                case "greaterthan": return $"{c} > {Literal(value.Trim(), numeric)}";
                case "greaterorequal": return $"{c} >= {Literal(value.Trim(), numeric)}";
                case "lessthan": return $"{c} < {Literal(value.Trim(), numeric)}";
                case "lessorequal": return $"{c} <= {Literal(value.Trim(), numeric)}";
                default:
                    error = $"'{op}' is not a supported filter.";
                    return null;
            }
        }

        private string Literal(string value, bool numeric) =>
            numeric && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : Quote(value);

        private string Quote(string value)
        {
            // MySQL and ClickHouse treat a backslash as an escape inside a string literal, so a value ending
            // in '\' would otherwise escape the closing quote.
            var escaped = _dialect is DataSourceType.MySQL or DataSourceType.ClickHouse
                ? value.Replace("\\", "\\\\").Replace("'", "''")
                : value.Replace("'", "''");
            return _dialect == DataSourceType.SQLServer ? $"N'{escaped}'" : $"'{escaped}'";
        }

        private string TextExpr(string quotedColumn) => _dialect switch
        {
            DataSourceType.SQLServer => $"CAST({quotedColumn} AS NVARCHAR(4000))",
            DataSourceType.ClickHouse => $"toString({quotedColumn})",
            DataSourceType.MySQL => $"CAST({quotedColumn} AS CHAR)",
            _ => $"CAST({quotedColumn} AS VARCHAR)"
        };
    }

    // Declared-type classification. Covers SQL spellings from every engine (INTEGER / int / Int64,
    // DECIMAL(18,2) / numeric) and the .NET type names a schema-only describe reports for ADO sources.
    private static readonly Regex NumericType = new(
        @"^(TINYINT|SMALLINT|INTEGER|INT|BIGINT|HUGEINT|UTINYINT|USMALLINT|UINTEGER|UBIGINT|UHUGEINT|U?INT\d+|LONG|SHORT|SERIAL|BIGSERIAL|DOUBLE|FLOAT\d*|REAL|DECIMAL|NUMERIC|MONEY|SMALLMONEY|NUMBER|BYTE|SBYTE|SINGLE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TextType = new(
        @"^(VARCHAR|NVARCHAR|CHAR|NCHAR|TEXT|NTEXT|STRING|CHARACTER|BPCHAR|LONGTEXT|MEDIUMTEXT|TINYTEXT|FIXEDSTRING|LOWCARDINALITY\(STRING|NULLABLE\(STRING)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsNumericType(string? dataType) =>
        !string.IsNullOrWhiteSpace(dataType) && NumericType.IsMatch(dataType.Trim());

    private static bool IsTextType(string? dataType) =>
        !string.IsNullOrWhiteSpace(dataType) && TextType.IsMatch(dataType.Trim());
}

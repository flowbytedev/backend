using System.Collections.Generic;

namespace Application.Shared.Models.Data;

/// <summary>
/// Body of <c>POST api/Datasets/{datasetId}/tables/{tableName}/pivot</c>: an Excel-style pivot layout over
/// one table. The server aggregates (GROUP BY) and reshapes; the browser only ever receives the pivoted cells.
/// </summary>
public class PivotRequest
{
    /// <summary>Fields down the left, outermost first.</summary>
    public List<PivotField> Rows { get; set; } = new();

    /// <summary>Fields across the top, outermost first. Each distinct combination becomes a column group.</summary>
    public List<PivotField> Columns { get; set; } = new();

    /// <summary>What to aggregate in each cell. Empty means a plain row count.</summary>
    public List<PivotValueField> Values { get; set; } = new();

    /// <summary>Conditions applied before aggregating. Same operator vocabulary as the data viewer, plus
    /// <c>notequals</c>, <c>isblank</c> and <c>notblank</c>; <c>equals</c> with <c>;</c>-separated values means IN.</summary>
    public List<FilterCondition> Filters { get; set; } = new();

    /// <summary>Read the External dataset's live source instead of its DuckDB snapshot.</summary>
    public bool LiveSource { get; set; }

    /// <summary>Add a total column per row and a grand-total row.</summary>
    public bool ShowTotals { get; set; } = true;
}

/// <summary>A row or column field. <see cref="DatePart"/> buckets a date/time column, as Excel's grouping does.</summary>
public class PivotField
{
    public string Column { get; set; } = string.Empty;

    /// <summary>One of <see cref="PivotDateParts"/>; empty for the raw value.</summary>
    public string? DatePart { get; set; }
}

/// <summary>A value field: the column to aggregate and how.</summary>
public class PivotValueField
{
    /// <summary>The column to aggregate, or <see cref="PivotValueField.AllRows"/> for a row count.</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>One of <see cref="PivotAggregations"/>.</summary>
    public string Aggregation { get; set; } = PivotAggregations.Sum;

    /// <summary>Pseudo-column meaning "count every row" (<c>COUNT(*)</c>).</summary>
    public const string AllRows = "*";
}

public static class PivotAggregations
{
    public const string Sum = "sum";
    public const string Avg = "avg";
    public const string Count = "count";
    public const string CountDistinct = "countDistinct";
    public const string Min = "min";
    public const string Max = "max";

    public static readonly IReadOnlyList<string> All = new[] { Sum, Avg, Count, CountDistinct, Min, Max };

    public static string Label(string aggregation) => aggregation switch
    {
        Sum => "Sum",
        Avg => "Average",
        Count => "Count",
        CountDistinct => "Distinct count",
        Min => "Min",
        Max => "Max",
        _ => aggregation
    };
}

public static class PivotDateParts
{
    public const string Year = "year";
    public const string Quarter = "quarter";
    public const string Month = "month";
    public const string Date = "date";

    public static readonly IReadOnlyList<string> All = new[] { Year, Quarter, Month, Date };

    public static string Label(string? part) => part switch
    {
        Year => "Year",
        Quarter => "Quarter",
        Month => "Month",
        Date => "Date",
        _ => string.Empty
    };
}

/// <summary>
/// A pivoted result, shaped for AG Grid. Row fields come back as <c>r0..rn</c>, value cells as
/// <c>v{columnKey}_{value}</c> and row totals as <c>t_{value}</c> — synthetic keys, because AG Grid reads a
/// dot in a field name as a nested path and real column values contain anything.
/// </summary>
public class PivotResult
{
    /// <summary>The row-field columns, in order (field <c>r{i}</c>).</summary>
    public List<PivotHeader> RowHeaders { get; set; } = new();

    /// <summary>The value columns, left to right, each with its header path for column grouping.</summary>
    public List<PivotColumn> Columns { get; set; } = new();

    /// <summary>Header text of each value field ("Sum of amount"), by value index.</summary>
    public List<string> ValueLabels { get; set; } = new();

    /// <summary>
    /// The pivoted rows. Besides <c>r{i}</c> and the value keys, each row carries <c>__o{i}</c>: the sort
    /// rank of its row-field value, so a month bucket labelled "Mar" still sorts after "Feb".
    /// </summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    /// <summary>The grand-total row, or null when totals were not requested or do not apply.</summary>
    public Dictionary<string, object?>? GrandTotal { get; set; }

    /// <summary>How many distinct column combinations the layout produced.</summary>
    public int ColumnKeyCount { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>The aggregate SQL that ran (the main query), for the curious and for support.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>Set when the pivot could not be built; nothing else is meaningful then.</summary>
    public string? Error { get; set; }
}

public class PivotHeader
{
    public string Field { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public class PivotColumn
{
    /// <summary>The row-dictionary key holding this column's cells.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Header labels from the outermost column field down; empty when there are no column fields.</summary>
    public List<string> Path { get; set; } = new();

    /// <summary>Which value field this column shows.</summary>
    public int ValueIndex { get; set; }

    /// <summary>True for the per-row total columns at the right.</summary>
    public bool IsTotal { get; set; }
}

using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The node-type catalogue: <b>the catalogue is the schema, the graph document is only the instance.</b>
/// One static definition drives the editor palette, the inspector's form fields, the compiler's port and
/// required-field validation, and the engine's executor dispatch. Served to the client at
/// <c>GET api/pipelines/node-types</c> so there are no hard-coded per-node Razor forms.
/// <para>
/// Metadata only — no executor lives here, which is what lets this sit in <c>Application.Shared</c> so the
/// web app, the scheduler and the browser can all validate the same graph with the same rules.
/// </para>
/// <para>
/// Adding a node type is one entry here plus one executor. If you find yourself editing a .razor file to
/// add a node, something has gone wrong.
/// </para>
/// </summary>
public static class PipelineNodeCatalog
{
    private static readonly Dictionary<string, PipelineNodeSpec> ByType;

    public static IReadOnlyList<PipelineNodeSpec> All { get; }

    public static bool IsKnown(string? type) => type is not null && ByType.ContainsKey(type);

    public static PipelineNodeSpec? Get(string? type) =>
        type is not null && ByType.TryGetValue(type, out var spec) ? spec : null;

    /// <summary>
    /// A node instance's output ports.
    /// <para>
    /// Static per type for everything except <c>transform.switch</c>, whose whole purpose is a
    /// configurable set of outputs. That exception is why this method takes a node rather than a type —
    /// it always could; nothing used the capability until now.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> OutPortsFor(PipelineNodeDef node) =>
        node.Type == PipelineNodeTypes.TransformSwitch
            ? PipelineSwitch.PortsFor(node)
            : Get(node.Type)?.OutPorts ?? [];

    /// <summary>A node instance's input ports. A join has two; a source has none.</summary>
    public static IReadOnlyList<string> InPortsFor(PipelineNodeDef node) => Get(node.Type)?.InPorts ?? [];

    /// <summary>
    /// The config keys the YAML view must treat as node configuration rather than node structure. Derived
    /// from the catalogue so a new field is automatically understood by YAML with no second list to update.
    /// </summary>
    public static IReadOnlyCollection<string> ConfigKeysFor(string? type) =>
        Get(type)?.Fields.Select(f => f.Key).ToArray() ?? [];

    static PipelineNodeCatalog()
    {
        var specs = new List<PipelineNodeSpec>
        {
            // ================= Sources =================
            // Sources have no input ports: they are the graph's entry points. A pipeline may have several.

            new(PipelineNodeTypes.SourceDataset, PipelineNodeCategories.Sources, "Dataset table",
                "Reads a table from another FlowByte dataset. A local dataset is read directly from its DuckDB file; an external one is queried through its database connection.",
                "database", [], [PipelinePorts.Out],
                [
                    new("dataset", "Dataset", PipelineFieldKinds.DatasetPicker, Required: true),
                    new("table", "Table", PipelineFieldKinds.TablePicker, Required: true, DependsOn: "dataset"),
                    new("columns", "Columns", PipelineFieldKinds.ColumnList,
                        Help: "Leave empty to read every column. Naming columns here reads less data."),
                    // Incremental reading. Only these two sources support it: both can be asked for
                    // MAX(column) before the load, which is what makes the window safe. A file or API
                    // source has nothing to ask.
                    new("incrementalColumn", "Only read rows newer than last time", PipelineFieldKinds.Text,
                        Placeholder: "modified_at",
                        Help: "A column that only ever increases - a timestamp or an ascending id. Each run reads rows above the previous run's highest value, so a nightly load stops re-reading the whole table. Leave empty to read everything every time."),
                    new("incrementalStart", "Start from", PipelineFieldKinds.Text,
                        Placeholder: "2026-01-01",
                        Help: "Where the first run begins. Empty means the first run reads the whole table."),
                    new("where", "Filter", PipelineFieldKinds.Text,
                        Placeholder: "sale_date >= '2026-01-01'",
                        Help: "Optional SQL condition applied at the source, so fewer rows are ever transferred.")
                ],
                IsSource: true),

            new(PipelineNodeTypes.SourceDatabase, PipelineNodeCategories.Sources, "Database query",
                "Reads from a registered database connection — SQL Server, PostgreSQL, MySQL, ClickHouse or DuckDB.",
                "server", [], [PipelinePorts.Out],
                [
                    new("connection", "Connection", PipelineFieldKinds.ConnectionPicker, Required: true),
                    new("mode", "Read", PipelineFieldKinds.Select, Required: true,
                        Options: [new("table", "A whole table"), new("query", "A SQL query")]),
                    new("schema", "Schema", PipelineFieldKinds.Text, Placeholder: "dbo", VisibleWhen: "mode=table"),
                    new("table", "Table", PipelineFieldKinds.Text, VisibleWhen: "mode=table"),
                    new("query", "Query", PipelineFieldKinds.Sql, VisibleWhen: "mode=query",
                        Placeholder: "SELECT * FROM dbo.sales WHERE sale_date >= '{{ run.date }}'"),
                    new("batchKeyColumn", "Batch by column", PipelineFieldKinds.Text,
                        Help: "A unique, sortable column. Set this for large tables so rows are paged out instead of buffered whole."),
                    // Incremental reading. Only these two sources support it: both can be asked for
                    // MAX(column) before the load, which is what makes the window safe. A file or API
                    // source has nothing to ask.
                    new("incrementalColumn", "Only read rows newer than last time", PipelineFieldKinds.Text,
                        Placeholder: "modified_at",
                        Help: "A column that only ever increases - a timestamp or an ascending id. Each run reads rows above the previous run's highest value, so a nightly load stops re-reading the whole table. Leave empty to read everything every time."),
                    new("incrementalStart", "Start from", PipelineFieldKinds.Text,
                        Placeholder: "2026-01-01",
                        Help: "Where the first run begins. Empty means the first run reads the whole table."),
                    new("batchSize", "Batch size", PipelineFieldKinds.Number, SupportsTokens: false,
                        Help: "Rows per page. Only used when a batch column is set."),
                    new("commandTimeoutSeconds", "Query timeout (s)", PipelineFieldKinds.Number, SupportsTokens: false)
                ],
                IsSource: true),

            new(PipelineNodeTypes.SourceFile, PipelineNodeCategories.Sources, "File",
                "Reads an Excel, CSV, TSV, JSON or Parquet file.",
                "file", [], [PipelinePorts.Out],
                [
                    new("location", "File is in", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new(PipelineFileLocations.Folder, "A server folder or share"),
                            new(PipelineFileLocations.Blob, "Azure Blob storage"),
                            new(PipelineFileLocations.Upload, "Uploaded when the pipeline is run")
                        ],
                        Help: "An uploaded file cannot be scheduled — there would be nothing to read when the schedule fired."),

                    new("path", "Path", PipelineFieldKinds.FilePath, VisibleWhen: "location=folder",
                        Placeholder: @"\\fileserver\drops\sales\*.xlsx",
                        Help: "A file, or a wildcard to match one."),
                    new("pick", "When several match", PipelineFieldKinds.Select, VisibleWhen: "location=folder",
                        Options: [new("newest", "Read the newest"), new("all", "Read and combine all")]),
                    new("archiveTo", "Move file here after success", PipelineFieldKinds.FilePath,
                        VisibleWhen: "location=folder",
                        Help: "Optional. Keeps a drop folder from re-reading the same file every night."),

                    new("container", "Container", PipelineFieldKinds.Text, VisibleWhen: "location=blob"),
                    new("blobPath", "Blob path", PipelineFieldKinds.Text, VisibleWhen: "location=blob",
                        Placeholder: "pipelines/sales/{{ run.date }}.xlsx"),

                    new("format", "Format", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new("csv", "CSV"), new("tsv", "TSV"), new("json", "JSON"),
                            new("parquet", "Parquet"), new("excel", "Excel (.xlsx)")
                        ]),
                    new("sheet", "Sheet", PipelineFieldKinds.Text, VisibleWhen: "format=excel",
                        Help: "Leave empty for the first sheet."),
                    new("hasHeader", "First row is a header", PipelineFieldKinds.Checkbox, SupportsTokens: false),

                    // Lineage. Only the file name needs engine support: it exists during the read and is
                    // gone afterwards. A run id or a load timestamp needs nothing new — an Add columns step
                    // with {{ run.id }} or {{ run.startedAt }} already does those, so they are not
                    // duplicated here.
                    new("addSourceFile", "Add a column with the file name", PipelineFieldKinds.Checkbox,
                        SupportsTokens: false,
                        Help: "Adds a filename column. Worth turning on whenever the path is a wildcard - it is the only way to tell afterwards which file a row came from."),

                    new("onBadRow", "If a row cannot be read", PipelineFieldKinds.Select,
                        VisibleWhen: "format=csv|tsv",
                        Options:
                        [
                            new(PipelineBadRowModes.Fail, "Fail the run"),
                            new(PipelineBadRowModes.Skip, "Skip it and count"),
                            new(PipelineBadRowModes.Quarantine, "Skip it and keep it for review")
                        ],
                        Help: "Failing is the default on purpose - dropping rows quietly is how a report ends up wrong. Quarantine keeps each rejected line, with the reason, in a rejects table."),
                    new("maxBadRows", "Fail if more than this many are bad", PipelineFieldKinds.Number,
                        VisibleWhen: "format=csv|tsv", SupportsTokens: false,
                        Help: "A safety net for skip and quarantine: a handful of bad rows is a data problem, thousands is a broken export that should not load at all. Empty means no limit."),

                    // Parse options. Only for the delimited formats — Excel, JSON and Parquet carry their
                    // own structure. Every one defaults to "let DuckDB decide", which is what the reader
                    // did before these existed, so an untouched pipeline is unaffected.
                    new("delimiter", "Column separator", PipelineFieldKinds.Text,
                        VisibleWhen: "format=csv|tsv", Placeholder: ",",
                        Help: "Leave empty to detect it - detection handles commas, semicolons and tabs reliably. Set it when a value can contain the separator, or to pin the format so a future file cannot be read differently. Write \\t for a tab."),
                    new("quote", "Quote character", PipelineFieldKinds.Text,
                        VisibleWhen: "format=csv|tsv", Placeholder: "\"",
                        Help: "Empty turns quoting off entirely - only for files that never quote."),
                    new("escape", "Escape character", PipelineFieldKinds.Text,
                        VisibleWhen: "format=csv|tsv", Placeholder: "\""),
                    new("nullString", "Text that means empty", PipelineFieldKinds.Text,
                        VisibleWhen: "format=csv|tsv", Placeholder: "NULL",
                        Help: "Exports often write NULL, \\N or - where they mean nothing at all."),
                    new("skipRows", "Lines to skip before the header", PipelineFieldKinds.Number,
                        VisibleWhen: "format=csv|tsv", SupportsTokens: false,
                        Help: "For a file with a title or timestamp preamble above the column names."),
                    new("encoding", "Encoding", PipelineFieldKinds.Select,
                        VisibleWhen: "format=csv|tsv",
                        Options:
                        [
                            new("", "Detect"), new("utf-8", "UTF-8"),
                            new("utf-16", "UTF-16"), new("latin-1", "Latin-1 / Windows-1252")
                        ],
                        Help: "Wrong encoding shows up as mangled accented characters, not as an error."),
                    new("compression", "Compression", PipelineFieldKinds.Select,
                        VisibleWhen: "format=csv|tsv",
                        Options:
                        [
                            new("", "Detect from the extension"), new("none", "None"),
                            new("gzip", "gzip"), new("zstd", "zstd")
                        ]),
                    new("dateFormat", "Date format", PipelineFieldKinds.Text,
                        VisibleWhen: "format=csv|tsv", Placeholder: "%d/%m/%Y",
                        Help: "Needed when day comes before month - otherwise 03/04 is read as 3 April or 4 March at random."),
                    new("timestampFormat", "Timestamp format", PipelineFieldKinds.Text,
                        VisibleWhen: "format=csv|tsv", Placeholder: "%d/%m/%Y %H:%M:%S"),
                    new("decimalSeparator", "Decimal separator", PipelineFieldKinds.Text,
                        VisibleWhen: "format=csv|tsv", Placeholder: ".",
                        Help: "Set to , for files written in the European convention."),
                    new("allText", "Read every column as text", PipelineFieldKinds.Checkbox,
                        VisibleWhen: "format=csv|tsv", SupportsTokens: false,
                        Help: "Turns off type detection so every value arrives as text, to cast yourself in a Map step. For a column whose type varies between files, or one detected as numeric that you need as text. Leading zeros are already preserved without this.")
                ],
                IsSource: true),

            new(PipelineNodeTypes.SourceApi, PipelineNodeCategories.Sources, "API",
                "Reads records from an HTTP endpoint, following pagination until the data runs out.",
                "cloud", [], [PipelinePorts.Out],
                [
                    new("credential", "Credential", PipelineFieldKinds.ApiCredentialPicker, Required: true,
                        Help: "A registered API credential. The token is held there, never in this pipeline."),
                    new("url", "URL or path", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "reports/sales?from={{ run.date }}",
                        Help: "A path under the credential's base URL, or a full URL on the same host."),
                    new("method", "Method", PipelineFieldKinds.Select,
                        Options: [new("GET", "GET"), new("POST", "POST")]),
                    new("headers", "Extra headers", PipelineFieldKinds.KeyValue,
                        Help: "Merged over the credential's own headers."),
                    new("body", "Request body", PipelineFieldKinds.TextArea, VisibleWhen: "method=POST",
                        Help: "Sent with each request, including each page. Written in whichever format the content type below declares."),
                    new("contentType", "Content type", PipelineFieldKinds.Text, VisibleWhen: "method=POST",
                        Placeholder: PipelineApiContentTypes.Json,
                        Help: "Free text, because this body is yours - a form body would be \"a=1&b=2\" with application/x-www-form-urlencoded. A charset is added if you do not give one. Defaults to application/json."),

                    new("jsonPath", "Records are at", PipelineFieldKinds.Text,
                        Placeholder: "data.items",
                        Help: "Dotted path to the array of records. Leave empty if the response IS the array."),

                    new("pagination", "Pagination", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new(PipelineApiPagination.None, "None - one request"),
                            new(PipelineApiPagination.Page, "Page number"),
                            new(PipelineApiPagination.Offset, "Row offset"),
                            new(PipelineApiPagination.Cursor, "Cursor from the response"),
                            new(PipelineApiPagination.LinkHeader, "Link header (rel=next)")
                        ]),

                    new("pageParam", "Page parameter", PipelineFieldKinds.Text,
                        VisibleWhen: "pagination=page", Placeholder: "page"),
                    new("offsetParam", "Offset parameter", PipelineFieldKinds.Text,
                        VisibleWhen: "pagination=offset", Placeholder: "offset"),
                    new("startPage", "First page number", PipelineFieldKinds.Number,
                        VisibleWhen: "pagination=page", SupportsTokens: false,
                        Help: "Most APIs start at 1; some start at 0."),

                    new("pageSizeParam", "Page size parameter", PipelineFieldKinds.Text,
                        VisibleWhen: "pagination=page|offset", Placeholder: "per_page"),
                    new("pageSize", "Page size", PipelineFieldKinds.Number,
                        VisibleWhen: "pagination=page|offset", SupportsTokens: false,
                        Help: "Also used to detect the last page: a short page ends the walk."),

                    new("cursorPath", "Cursor is at", PipelineFieldKinds.Text,
                        VisibleWhen: "pagination=cursor", Placeholder: "meta.next_cursor",
                        Help: "Dotted path to the next-page token in the response body."),
                    new("cursorParam", "Cursor parameter", PipelineFieldKinds.Text,
                        VisibleWhen: "pagination=cursor", Placeholder: "cursor"),

                    new("flatten", "Nested values", PipelineFieldKinds.Select,
                        Options:
                        [
                            new(PipelineApiFlatten.OneLevel, "Flatten one level (customer_id)"),
                            new(PipelineApiFlatten.All, "Flatten every level"),
                            new(PipelineApiFlatten.None, "Keep as JSON text")
                        ],
                        Help: "Arrays always stay JSON text - flattening them would make the column count depend on the data.")
                ],
                IsSource: true),

            // ================= Shape =================

            new(PipelineNodeTypes.TransformMap, PipelineNodeCategories.Shape, "Map columns",
                "Rename, retype, reorder and drop columns. This is where source names become destination names.",
                "columns", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("columns", "Columns", PipelineFieldKinds.ColumnMap, Required: true,
                        Help: "One row per output column: pick a source column and optionally a type to cast to, or supply a fixed value."),
                    new("keepUnmapped", "Keep columns not listed above", PipelineFieldKinds.Checkbox, SupportsTokens: false,
                        Help: "On, this node only renames and retypes what you listed. Off, anything unlisted is dropped."),
                    new("drop", "Drop", PipelineFieldKinds.ColumnList, VisibleWhen: "keepUnmapped=true",
                        Help: "Columns to remove even though unlisted columns are kept.")
                ]),

            new(PipelineNodeTypes.TransformFilter, PipelineNodeCategories.Shape, "Filter rows",
                "Keeps only the rows matching a condition.",
                "filter", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("where", "Keep rows where", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "qty > 0 AND region IN ('N','S')")
                ]),

            new(PipelineNodeTypes.TransformCompute, PipelineNodeCategories.Shape, "Add columns",
                "Adds columns calculated from the ones already there.",
                "calculator", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("columns", "New columns", PipelineFieldKinds.ExpressionList, Required: true,
                        Help: "Each row is a new column name and an expression, e.g. qty * unit_price.")
                ]),


            new(PipelineNodeTypes.TransformSort, PipelineNodeCategories.Shape, "Sort rows",
                "Orders the rows. Worth doing before a Remove duplicates or a running total, where the order decides the answer.",
                "arrow-down-up", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("columns", "Sort by", PipelineFieldKinds.SortList, Required: true,
                        Help: "Applied in order: the first column decides, later ones break ties.")
                ]),

            new(PipelineNodeTypes.TransformRank, PipelineNodeCategories.Shape, "Rank rows",
                "Numbers rows by an ordering, optionally restarting within each group.",
                "medal", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("column", "New column", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "rank"),
                    new("method", "When values tie", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new("rank", "Same rank, then skip (1, 2, 2, 4)"),
                            new("dense_rank", "Same rank, no gap (1, 2, 2, 3)"),
                            new("row_number", "Always different (1, 2, 3, 4)")
                        ],
                        Help: "Matches Power Query's Standard, Dense and Ordinal."),
                    new("orderBy", "Rank by", PipelineFieldKinds.SortList, Required: true),
                    new("partitionBy", "Restart for each", PipelineFieldKinds.ColumnList,
                        Help: "Leave empty to rank across the whole table.")
                ]),

            new(PipelineNodeTypes.TransformSurrogateKey, PipelineNodeCategories.Shape, "Add a row number",
                "Adds a sequential number to each row - the surrogate key of a dimension table.",
                "hash", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("column", "New column", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "row_key"),
                    new("startAt", "Start at", PipelineFieldKinds.Number, SupportsTokens: false),
                    new("orderBy", "Number in this order", PipelineFieldKinds.SortList,
                        Help: "Strongly recommended. Without an order the numbering can differ between runs, which makes it useless as a key.")
                ]),

            new(PipelineNodeTypes.TransformWindow, PipelineNodeCategories.Shape, "Running totals",
                "Adds a column computed across related rows - a running total, a group total on every row, the previous row's value.",
                "trending-up", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("columns", "Columns to add", PipelineFieldKinds.WindowList, Required: true),
                    new("partitionBy", "Calculate within each", PipelineFieldKinds.ColumnList,
                        Help: "Leave empty to calculate across the whole table."),
                    new("orderBy", "In this order", PipelineFieldKinds.SortList,
                        Help: "Required for a running total or a previous/next value."),
                    new("cumulative", "Accumulate down the rows", PipelineFieldKinds.Checkbox,
                        SupportsTokens: false,
                        Help: "On gives a running total. Off gives the same total on every row of the group.")
                ]),

            new(PipelineNodeTypes.TransformFill, PipelineNodeCategories.Shape, "Fill gaps",
                "Carries the previous value into empty cells - the usual fix for a report export that only prints a label when it changes.",
                "arrow-down-to-line", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("columns", "Columns to fill", PipelineFieldKinds.ColumnList, Required: true),
                    new("direction", "Fill", PipelineFieldKinds.Select, Required: true,
                        Options: [new("down", "Downwards"), new("up", "Upwards")]),
                    new("orderBy", "Row order", PipelineFieldKinds.SortList, Required: true,
                        Help: "\"The previous value\" means nothing without a defined order."),
                    new("partitionBy", "Do not fill across", PipelineFieldKinds.ColumnList,
                        Help: "Filling restarts at each new value of these columns - so one store's last value never leaks into the next."),
                    new("_note", "Empty means NULL", PipelineFieldKinds.Note,
                        Help: "Only real NULLs are filled. An empty string looks blank but is a value - replace it with NULL first, in an Add columns step.")
                ]),

            new(PipelineNodeTypes.TransformText, PipelineNodeCategories.Shape, "Clean up text",
                "Trims, changes case, or replaces text in place.",
                "type", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("operations", "Operations", PipelineFieldKinds.TextOpList, Required: true,
                        Help: "Leave the target column empty to change the column in place.")
                ]),

            // ================= Reshape =================
            // These change the shape of the table - the columns or the row count - rather than the values.

            new(PipelineNodeTypes.TransformSplit, PipelineNodeCategories.Reshape, "Split a column",
                "Splits one text column on a separator, into several columns or into several rows.",
                "split", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("column", "Column to split", PipelineFieldKinds.Text, Required: true),
                    new("delimiter", "Separator", PipelineFieldKinds.Text, Required: true,
                        Placeholder: ","),
                    new("mode", "Split into", PipelineFieldKinds.Select, Required: true,
                        Options: [new("columns", "Columns"), new("rows", "Rows")]),
                    new("columns", "New column names", PipelineFieldKinds.ColumnNameList,
                        VisibleWhen: "mode=columns",
                        Help: "How many names you give is how many parts are taken; anything beyond them is dropped."),
                    new("into", "New column name", PipelineFieldKinds.Text, VisibleWhen: "mode=rows",
                        Help: "The original column is replaced by this one, with a row per part.")
                ]),

            new(PipelineNodeTypes.TransformPivot, PipelineNodeCategories.Reshape, "Values to columns",
                "Turns the values of one column into columns of their own - months across the top instead of down the side.",
                "columns-3", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("on", "Column whose values become columns", PipelineFieldKinds.Text, Required: true),
                    new("function", "Combine values with", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new("sum", "Sum"), new("count", "Count"), new("avg", "Average"),
                            new("min", "Minimum"), new("max", "Maximum")
                        ]),
                    new("value", "Column to combine", PipelineFieldKinds.Text,
                        Help: "Not needed when counting."),
                    new("groupBy", "One row per", PipelineFieldKinds.ColumnList,
                        Help: "The columns that stay down the side."),
                    new("_note", "The columns depend on the data", PipelineFieldKinds.Note,
                        Help: "The new column names come from the values found at run time, so this step's columns can change between runs. A later step that names them may need updating when the data does.")
                ]),

            new(PipelineNodeTypes.TransformUnpivot, PipelineNodeCategories.Reshape, "Columns to values",
                "The reverse: turns a set of columns into two - one holding the old column name, one holding the value.",
                "rows-3", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("columns", "Columns to turn into rows", PipelineFieldKinds.ColumnList, Required: true),
                    new("nameColumn", "Column for the name", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "attribute"),
                    new("valueColumn", "Column for the value", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "value")
                ]),

            new(PipelineNodeTypes.TransformFlatten, PipelineNodeCategories.Reshape, "List to rows",
                "Turns a column holding a list into one row per item. This is what an API source's array columns need.",
                "list", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("column", "Column holding the list", PipelineFieldKinds.Text, Required: true),
                    new("into", "New column name", PipelineFieldKinds.Text,
                        Help: "Empty keeps the original name.")
                ]),

            new(PipelineNodeTypes.TransformParse, PipelineNodeCategories.Reshape, "Read JSON fields",
                "Pulls named fields out of a column holding JSON text - the deeper values an API source kept as text.",
                "braces", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("column", "Column holding JSON", PipelineFieldKinds.Text, Required: true),
                    new("fields", "Fields to pull out", PipelineFieldKinds.KeyValue, Required: true,
                        Help: "New column name on the left, path on the right - customer.address.city, or just id for a top-level field."),
                    new("dropSource", "Remove the JSON column", PipelineFieldKinds.Checkbox,
                        SupportsTokens: false)
                ]),

            new(PipelineNodeTypes.TransformSwitch, PipelineNodeCategories.Reshape, "Route rows",
                "Sends each row to one of several outputs depending on a condition. Every row goes to exactly one output; anything matching nothing goes to the leftover output.",
                // The spec declares the shape a FRESH node has; OutPortsFor refines it from the config once
                // outputs are named. Declaring [] here would be truthful for a moment and wrong afterwards,
                // and it breaks the catalogue invariant that a non-terminal step has somewhere to send rows.
                "git-fork", [PipelinePorts.In], ["match", PipelineSwitch.DefaultPort],
                [
                    new("outputs", "Outputs", PipelineFieldKinds.SwitchList, Required: true,
                        Help: "Checked top to bottom - the first match wins, so put the most specific condition first."),
                    new("_note", "Nothing is dropped", PipelineFieldKinds.Note,
                        Help: "A row matching no condition goes to the built-in \"rest\" output. Leave it unconnected and those rows stop there; connect it to keep them.")
                ]),

            // ================= Combine =================

            new(PipelineNodeTypes.TransformJoin, PipelineNodeCategories.Combine, "Join / lookup",
                "Matches rows against a second input and brings its columns across.",
                "git-merge", [PipelinePorts.Left, PipelinePorts.Right], [PipelinePorts.Out],
                [
                    new("kind", "Join type", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new("left", "Keep every left row (left join)"),
                            new("inner", "Only matching rows (inner join)"),
                            new("anti", "Only left rows with NO match (anti join)")
                        ]),
                    new("on", "Match on", PipelineFieldKinds.JoinKeys, Required: true,
                        Help: "One or more column pairs that must be equal."),
                    new("bring", "Bring across", PipelineFieldKinds.KeyValue,
                        Help: "Right-hand columns to keep, and what to call them. Leave empty to bring all of them."),
                    new("suffix", "Suffix for clashing names", PipelineFieldKinds.Text, Placeholder: "_r",
                        Help: "Only used when bringing all columns across.")
                ]),

            new(PipelineNodeTypes.TransformUnion, PipelineNodeCategories.Combine, "Stack rows",
                "Appends several inputs into one relation. Connect as many inputs as you need.",
                "layers", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("mode", "Duplicates", PipelineFieldKinds.Select,
                        Options: [new("all", "Keep duplicate rows"), new("distinct", "Remove duplicate rows")]),
                    new("byName", "Match columns by name", PipelineFieldKinds.Checkbox, SupportsTokens: false,
                        Help: "On, columns line up by name and missing ones become null. Off, they line up by position and every input must have the same shape.")
                ],
                AllowsMultipleInputs: true),

            // ================= Summarize =================

            new(PipelineNodeTypes.TransformDedupe, PipelineNodeCategories.Summarize, "Remove duplicates",
                "Keeps one row per set of key columns.",
                "copy-minus", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("keys", "Duplicate when these match", PipelineFieldKinds.ColumnList, Required: true),
                    new("keep", "Which one to keep", PipelineFieldKinds.Select,
                        Options: [new("first", "The first"), new("last", "The last")]),
                    new("orderBy", "Ordered by", PipelineFieldKinds.Text,
                        Placeholder: "modified_at",
                        Help: "Which column decides first and last. Without it the choice is arbitrary.")
                ]),

            new(PipelineNodeTypes.TransformAggregate, PipelineNodeCategories.Summarize, "Group and summarize",
                "Collapses rows into one per group, with totals, counts and averages.",
                "sigma", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("groupBy", "Group by", PipelineFieldKinds.ColumnList, Required: true),
                    new("metrics", "Summaries", PipelineFieldKinds.AggregateList, Required: true,
                        Help: "Each row is an output column, a function, and the column to apply it to.")
                ]),

            // ================= SQL =================

            new(PipelineNodeTypes.TransformSql, PipelineNodeCategories.Sql, "SQL",
                "Runs your own SELECT over the upstream data. Refer to an input by its node name.",
                "code", [PipelinePorts.In], [PipelinePorts.Out],
                [
                    new("sql", "Query", PipelineFieldKinds.Sql, Required: true,
                        Placeholder: "SELECT store_id, SUM(qty) AS qty FROM erp GROUP BY store_id",
                        Help: "Read-only. Every connected input is available as a table named after its node.")
                ],
                AllowsMultipleInputs: true),

            // ================= Destination =================
            // Terminal. Note there is deliberately no column-mapping field here: mapping belongs to a
            // "Map columns" node so there is exactly one place in the graph where renaming happens, and
            // so the mapping is visible on the canvas rather than buried in the last node's settings.

            new(PipelineNodeTypes.DestinationDataset, PipelineNodeCategories.Destination, "Write to dataset",
                "Writes the incoming rows into a dataset table, matching columns by name.",
                "download", [PipelinePorts.In], [],
                [
                    new("dataset", "Dataset", PipelineFieldKinds.DatasetPicker, Required: true),
                    new("table", "Table", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "item  or  product.item",
                        Help: "An existing table, or a new name if you let it be created below. Write schema.table to choose a schema - a plain name uses the connection's default. For ClickHouse and MySQL the first part is the database."),
                    new("mode", "How to write", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new(PipelineWriteModes.Append, "Add to what is there"),
                            new(PipelineWriteModes.Replace, "Replace everything"),
                            new(PipelineWriteModes.Upsert, "Update matching rows, add the rest")
                        ]),
                    new("keys", "Rows match on", PipelineFieldKinds.ColumnList,
                        VisibleWhen: $"mode={PipelineWriteModes.Upsert}",
                        Help: "The columns that identify the same row across runs."),
                    new("createIfMissing", "Create the table if it does not exist", PipelineFieldKinds.Checkbox,
                        SupportsTokens: false,
                        Help: "For an external dataset this issues CREATE TABLE inside that database, so it is off unless you turn it on.")
                ],
                IsTerminal: true),

            new(PipelineNodeTypes.DestinationApi, PipelineNodeCategories.Destination, "Send to API",
                "Sends the incoming rows to an HTTP endpoint. Unlike a dataset write this cannot be rolled back.",
                "upload-cloud", [PipelinePorts.In], [],
                [
                    new("credential", "Credential", PipelineFieldKinds.ApiCredentialPicker, Required: true,
                        Help: "Must have \"May send data\" enabled. A credential without it is refused, not downgraded."),
                    new("url", "URL or path", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "v2/orders/bulk"),
                    new("method", "Method", PipelineFieldKinds.Select, Required: true,
                        Options: [new("POST", "POST"), new("PUT", "PUT"), new("PATCH", "PATCH")]),
                    new("headers", "Extra headers", PipelineFieldKinds.KeyValue),

                    // A closed list, unlike the source's: this body is BUILT from rows, so the only content
                    // types on offer are the ones there is a serializer for. Offering XML here would be a
                    // promise with nothing behind it.
                    new("contentType", "Send as", PipelineFieldKinds.Select,
                        Options:
                        [
                            new(PipelineApiContentTypes.Json, "JSON"),
                            new(PipelineApiContentTypes.Form, "Form (application/x-www-form-urlencoded)")
                        ],
                        Help: "Form encoding cannot express a list, so it always sends one request per row."),

                    new("shape", "Send rows as", PipelineFieldKinds.Select, Required: true,
                        VisibleWhen: $"contentType={PipelineApiContentTypes.Json}",
                        Options:
                        [
                            new(PipelineApiWriteShapes.Batch, "A batch per request"),
                            new(PipelineApiWriteShapes.Row, "One request per row")
                        ]),
                    new("batchSize", "Rows per request", PipelineFieldKinds.Number,
                        // BOTH conditions: a form-encoded step sends one row per request whatever shape
                        // says, so showing "rows per request" there would be a control that does nothing.
                        VisibleWhen: $"contentType={PipelineApiContentTypes.Json}&shape=batch",
                        SupportsTokens: false),
                    new("bodyProperty", "Wrap the batch in", PipelineFieldKinds.Text,
                        Placeholder: "records",
                        Help: "JSON: sends a named property holding the array. Form: prefixes every field, so \"record\" sends record[sku]=... Leave empty for neither."),

                    new("stopOnError", "Stop at the first failed request", PipelineFieldKinds.Checkbox,
                        SupportsTokens: false,
                        Help: "On by default. Rows already accepted stay accepted either way - there is no rollback over HTTP.")
                ],
                IsTerminal: true),

            // Like destination.api this has no write mode, and for a stronger reason: an email cannot be
            // recalled at all. It also has no dataset and no table - the artefact it produces is a file,
            // which is why the size fields below exist and no other destination needs them.
            new(PipelineNodeTypes.DestinationEmail, PipelineNodeCategories.Destination, "Email a file",
                "Exports the incoming rows as a file and emails it. Cannot be undone once sent.",
                "mail", [PipelinePorts.In], [],
                [
                    new("to", "To", PipelineFieldKinds.StringList, Required: true,
                        Placeholder: "someone@example.com",
                        Help: "One address per row. Commas and semicolons in a row are split too."),
                    new("cc", "Cc", PipelineFieldKinds.StringList),
                    new("bcc", "Bcc", PipelineFieldKinds.StringList),
                    new("replyTo", "Reply to", PipelineFieldKinds.StringList,
                        Help: "Where replies go. Leave empty to use the sending address."),

                    new("subject", "Subject", PipelineFieldKinds.Text, Required: true,
                        Placeholder: "Daily orders - {{ run.date }}"),
                    new("body", "Message", PipelineFieldKinds.TextArea,
                        Help: "Shown above the summary. Plain text - it is escaped, not rendered as HTML."),

                    new("format", "Attach as", PipelineFieldKinds.Select, Required: true,
                        Options:
                        [
                            new(PipelineExportFormats.Csv, "CSV"),
                            new(PipelineExportFormats.Xlsx, "Excel (.xlsx)"),
                            new(PipelineExportFormats.Json, "JSON")
                        ]),
                    new("fileName", "File name", PipelineFieldKinds.Text,
                        Placeholder: "orders_{{ run.date }}",
                        Help: "Without the extension - the format adds it. Defaults to the pipeline name and run date."),
                    new("delimiter", "Delimiter", PipelineFieldKinds.Text,
                        VisibleWhen: $"format={PipelineExportFormats.Csv}", Placeholder: ",",
                        Help: "One character. Write \\t for a tab."),
                    new("includeHeader", "Include the header row", PipelineFieldKinds.Checkbox,
                        VisibleWhen: $"format={PipelineExportFormats.Csv}", SupportsTokens: false,
                        Help: "On unless you turn it off."),
                    new("sheetName", "Sheet name", PipelineFieldKinds.Text,
                        VisibleWhen: $"format={PipelineExportFormats.Xlsx}", Placeholder: "Data",
                        Help: "Excel rejects a name over 31 characters or containing : \\ / ? * [ ]; those are replaced."),
                    new("compress", "Zip the file", PipelineFieldKinds.Checkbox, SupportsTokens: false,
                        Help: "A delimited export usually shrinks 5-10x, which is the difference between fitting in an email and not."),

                    new("onEmpty", "When there are no rows", PipelineFieldKinds.Select,
                        Options:
                        [
                            new(PipelineEmailEmptyBehaviour.Send, "Send it anyway"),
                            new(PipelineEmailEmptyBehaviour.Skip, "Send nothing, succeed"),
                            new(PipelineEmailEmptyBehaviour.Fail, "Fail the step")
                        ],
                        Help: "Sending an empty file is the default, because a mail that arrives proves the schedule ran."),

                    new("onOversize", "When it is too big to attach", PipelineFieldKinds.Select,
                        Options:
                        [
                            new(PipelineEmailOversizeBehaviour.Fail, "Fail the step"),
                            new(PipelineEmailOversizeBehaviour.DatasetLink, "Write to a dataset and send a link")
                        ],
                        Help: "There is a hard size ceiling on an attachment that this app does not set. A link keeps the recipient's dataset permissions; an attachment has none."),
                    new("linkDataset", "Write it to", PipelineFieldKinds.DatasetPicker,
                        VisibleWhen: $"onOversize={PipelineEmailOversizeBehaviour.DatasetLink}"),
                    new("linkTable", "Table", PipelineFieldKinds.Text,
                        VisibleWhen: $"onOversize={PipelineEmailOversizeBehaviour.DatasetLink}",
                        Placeholder: "orders_export",
                        Help: "Replaced on every run, so the linked table always matches the email that pointed at it. Created if missing.")
                ],
                IsTerminal: true)
        };

        All = specs;
        ByType = specs.ToDictionary(s => s.Type, StringComparer.Ordinal);
    }
}

/// <summary>One node type's definition. Serialized to the client verbatim.</summary>
public sealed record PipelineNodeSpec(
    string Type,
    string Category,
    string Label,
    string Description,
    /// <summary>Icon key; the client maps it to an inline stroke SVG.</summary>
    string Icon,
    IReadOnlyList<string> InPorts,
    IReadOnlyList<string> OutPorts,
    IReadOnlyList<PipelineFieldSpec> Fields,
    /// <summary>Reads data in rather than from another node. Sources have no input ports.</summary>
    bool IsSource = false,
    /// <summary>Ends a path. Terminal nodes may have no outgoing edges.</summary>
    bool IsTerminal = false,
    /// <summary>
    /// The <c>in</c> port accepts more than one edge (stack rows, SQL). Everything else takes exactly one
    /// edge per port — silently reading only the first of two would be a very confusing bug.
    /// </summary>
    bool AllowsMultipleInputs = false);

/// <summary>One config field, as rendered by the inspector.</summary>
public sealed record PipelineFieldSpec(
    string Key,
    string Label,
    string Kind,
    bool Required = false,
    string? Placeholder = null,
    string? Help = null,
    IReadOnlyList<PipelineFieldOption>? Options = null,
    /// <summary>False for fields where a <c>{{ run.* }}</c> token makes no sense (numbers, toggles).</summary>
    bool SupportsTokens = true,
    /// <summary>
    /// Only shown when another field has this value, written <c>key=value</c>. Keeps the file node from
    /// presenting folder and blob settings at the same time.
    /// </summary>
    string? VisibleWhen = null,
    /// <summary>
    /// This field's options are loaded using another field's value — a table picker needs the chosen
    /// dataset. The inspector clears this field when the named field changes.
    /// </summary>
    string? DependsOn = null);

public sealed record PipelineFieldOption(string Value, string Label);

/// <summary>
/// The vocabulary of inspector controls. Deliberately small: anything that cannot be a form control
/// belongs in a structured list, not in an expression language the visual editor cannot author.
/// </summary>
public static class PipelineFieldKinds
{
    public const string Text = "text";
    public const string TextArea = "textarea";
    public const string Number = "number";
    public const string Checkbox = "checkbox";
    public const string Select = "select";
    public const string KeyValue = "keyvalue";

    /// <summary>Monaco, SQL mode.</summary>
    public const string Sql = "sql";

    /// <summary>A server path or share, with a wildcard allowed.</summary>
    public const string FilePath = "filepath";

    // ---- Pickers. Options come from an api/* endpoint, not from the spec. ----
    public const string DatasetPicker = "dataset";
    public const string TablePicker = "table";
    public const string ConnectionPicker = "connection";

    /// <summary>A registered <c>ApiCredential</c>, by name. Options from api/pipelines/api-credentials.</summary>
    public const string ApiCredentialPicker = "apicredential";

    // ---- Structured editors, each backed by the upstream node's cached columns. ----

    /// <summary>Rows of (output name, source column | constant, cast type). The mapping grid.</summary>
    public const string ColumnMap = "columnmap";

    /// <summary>A multi-select of upstream column names.</summary>
    public const string ColumnList = "columnlist";

    /// <summary>Rows of (new column name, SQL expression).</summary>
    public const string ExpressionList = "expressionlist";

    /// <summary>Rows of (output name, function, column).</summary>
    public const string AggregateList = "aggregatelist";

    /// <summary>Rows of (left column, right column).</summary>
    public const string JoinKeys = "joinkeys";

    /// <summary>Rows of (column, direction, nulls-first/last). The sort grid.</summary>
    public const string SortList = "sortlist";

    /// <summary>Rows of (output name, condition). The routing grid.</summary>
    public const string SwitchList = "switchlist";

    /// <summary>Rows of (output name, function, column). The window grid.</summary>
    public const string WindowList = "windowlist";

    /// <summary>Rows of (column, operation, find, replace, into). The text-cleanup grid.</summary>
    public const string TextOpList = "textoplist";

    /// <summary>A free list of new column names, not chosen from the incoming schema.</summary>
    public const string ColumnNameList = "columnnamelist";

    /// <summary>
    /// A free list of plain strings — email recipients, and anything else that is a list of values rather
    /// than a list of columns. Shares <see cref="ColumnNameList"/>'s editor; separate because the field's
    /// own placeholder and add-row label make sense only when the kind says what the values are.
    /// </summary>
    public const string StringList = "stringlist";

    /// <summary>
    /// Not an input at all — help text the inspector renders on its own. For a caveat that belongs beside a
    /// step rather than buried in one field's help, like pivot's data-dependent columns.
    /// </summary>
    public const string Note = "note";
}

/// <summary>Functions offered by the "Group and summarize" node. Rendered as a dropdown.</summary>
public static class PipelineAggregateFunctions
{
    public const string Sum = "sum";
    public const string Count = "count";
    public const string CountDistinct = "count_distinct";
    public const string Avg = "avg";
    public const string Min = "min";
    public const string Max = "max";

    public static readonly string[] All = [Sum, Count, CountDistinct, Avg, Min, Max];
}

namespace Application.Shared.Models.Data.Pipelines;

/// <summary>
/// Every node type a pipeline graph can contain. These strings are the contract: they appear in stored
/// graph JSON, in hand-written YAML, and as the dispatch key for executors, so a value here can only be
/// renamed alongside a <see cref="PipelineGraph.CurrentSchemaVersion"/> bump.
/// </summary>
public static class PipelineNodeTypes
{
    // ---- Sources: in-degree 0. A graph needs at least one, and may have several. ------------------
    public const string SourceDataset = "source.dataset";
    public const string SourceDatabase = "source.database";
    public const string SourceFile = "source.file";
    public const string SourceApi = "source.api";

    // ---- Shape: one input, one output, column-level work. -----------------------------------------
    public const string TransformMap = "transform.map";
    public const string TransformFilter = "transform.filter";
    public const string TransformCompute = "transform.compute";

    // ---- Combine: more than one input. ------------------------------------------------------------
    public const string TransformJoin = "transform.join";
    public const string TransformUnion = "transform.union";

    // ---- Summarize: fewer rows out than in. -------------------------------------------------------
    public const string TransformDedupe = "transform.dedupe";
    public const string TransformAggregate = "transform.aggregate";

    // ---- Shape, round two. Each was already writable in transform.sql; these make it clickable. ----
    public const string TransformSort = "transform.sort";
    public const string TransformRank = "transform.rank";
    public const string TransformSurrogateKey = "transform.surrogatekey";
    public const string TransformWindow = "transform.window";
    public const string TransformFill = "transform.fill";
    public const string TransformText = "transform.text";
    public const string TransformSplit = "transform.split";

    // ---- Reshape: the row/column count changes. ---------------------------------------------------
    public const string TransformPivot = "transform.pivot";
    public const string TransformUnpivot = "transform.unpivot";
    public const string TransformFlatten = "transform.flatten";
    public const string TransformParse = "transform.parse";

    /// <summary>
    /// Routes rows to one of several outputs. The only node type with more than one output port, which is
    /// why it is the one that needed engine work rather than just a SQL builder.
    /// </summary>
    public const string TransformSwitch = "transform.switch";

    // ---- Escape hatch. ---------------------------------------------------------------------------
    public const string TransformSql = "transform.sql";

    // ---- Destinations: terminal. A graph needs at least one. --------------------------------------
    public const string DestinationDataset = "destination.dataset";
    public const string DestinationApi = "destination.api";
    public const string DestinationEmail = "destination.email";

    /// <summary>True for the node types that read data in rather than from another node.</summary>
    public static bool IsSource(string? type) =>
        type is SourceDataset or SourceDatabase or SourceFile or SourceApi;

    /// <summary>True for terminal node types — nothing may be connected downstream of these.</summary>
    public static bool IsDestination(string? type) =>
        type is DestinationDataset or DestinationApi or DestinationEmail;
}

/// <summary>Sort direction and null placement for the sort step.</summary>
public static class PipelineSortDirections
{
    public const string Ascending = "asc";
    public const string Descending = "desc";

    public static readonly string[] All = [Ascending, Descending];
}

/// <summary>Palette groupings, in the order the editor renders them.</summary>
public static class PipelineNodeCategories
{
    public const string Sources = "Sources";
    public const string Shape = "Shape";
    public const string Combine = "Combine";
    public const string Summarize = "Summarize";
    public const string Sql = "SQL";
    public const string Destination = "Destination";

    /// <summary>
    /// Steps that change the shape of the table rather than its values — pivot, unpivot, flatten, parse,
    /// split. Separated from Shape because the distinction people actually care about when hunting for a
    /// step is "does this change my columns or my rows".
    /// </summary>
    public const string Reshape = "Reshape";

    public static readonly string[] Ordered =
        [Sources, Shape, Reshape, Combine, Summarize, Sql, Destination];
}

/// <summary>
/// Well-known port names. Unlike a generic workflow, most pipeline nodes take exactly one input, so
/// <see cref="In"/> covers the common case; <see cref="Left"/>/<see cref="Right"/> exist because a join's
/// two inputs are not interchangeable and the graph must record which is which.
/// </summary>
public static class PipelinePorts
{
    public const string In = "in";

    /// <summary>Join: the side whose rows are all preserved by a left join.</summary>
    public const string Left = "left";

    /// <summary>Join: the lookup side.</summary>
    public const string Right = "right";

    public const string Out = "out";
}

/// <summary>
/// How a node's data is written into an existing destination table. Mirrors the existing
/// <see cref="ImportMode"/> used by file import and scheduled ingestion, deliberately reusing the same
/// three words so the two features don't drift into different vocabularies.
/// </summary>
public static class PipelineWriteModes
{
    public const string Append = "append";
    public const string Replace = "replace";
    public const string Upsert = "upsert";

    public static readonly string[] All = [Append, Replace, Upsert];
}

/// <summary>Where a <c>source.file</c> node's file comes from.</summary>
public static class PipelineFileLocations
{
    /// <summary>A server path or UNC share, optionally a glob. Schedulable.</summary>
    public const string Folder = "folder";

    /// <summary>Azure Blob container + path. Schedulable.</summary>
    public const string Blob = "blob";

    /// <summary>Attached by the user at run time. NOT schedulable — the compiler rejects it with a cron.</summary>
    public const string Upload = "upload";

    public static readonly string[] All = [Folder, Blob, Upload];

    /// <summary>
    /// True when a file at this location still exists at an arbitrary later time. An <see cref="Upload"/>
    /// does not, which is why a pipeline containing one cannot be given a cron or API trigger.
    /// </summary>
    public static bool IsSchedulable(string? location) => location is Folder or Blob;
}

/// <summary>
/// What a source step does with a row it cannot parse.
/// <para>
/// <see cref="Fail"/> is the default and stays the default: silently dropping rows from a load is how a
/// report ends up quietly wrong, and that should be something an operator chooses rather than inherits.
/// </para>
/// </summary>
public static class PipelineBadRowModes
{
    /// <summary>Fail the step, naming the line. Today's behaviour, and the default.</summary>
    public const string Fail = "fail";

    /// <summary>Skip the row and count it. The count appears on the step, so a partial load is visible.</summary>
    public const string Skip = "skip";

    /// <summary>
    /// Skip the row, count it, AND keep it — with the reason — in a rejects table beside the destination, so
    /// it can be looked at and reloaded once whatever produced it is fixed.
    /// </summary>
    public const string Quarantine = "quarantine";

    public static readonly string[] All = [Fail, Skip, Quarantine];

    /// <summary>True when the reader should carry on past a malformed row rather than stopping.</summary>
    public static bool Tolerates(string? mode) => mode is Skip or Quarantine;

    /// <summary>True when the skipped rows must be kept rather than merely counted.</summary>
    public static bool Keeps(string? mode) => mode == Quarantine;
}

/// <summary>What a node does when it fails.</summary>
public static class PipelineErrorMode
{
    /// <summary>Fail the run. Anything not yet started is skipped. The default, and the right one for ETL.</summary>
    public const string Fail = "fail";

    /// <summary>
    /// Carry on. Nodes downstream of the failure are still skipped — they have no input relation to read,
    /// so "continue" can only mean "let unrelated branches finish", never "run with empty data".
    /// </summary>
    public const string Continue = "continue";

    public static readonly string[] All = [Fail, Continue];
}

/// <summary>
/// How a <c>source.api</c> step walks a multi-page endpoint. Stored strings, so a contract.
/// <para>
/// Four styles rather than one because there is no dominant convention: page numbers and offsets are the
/// house style of most internal APIs, opaque cursors are what the large public APIs use, and the Link header
/// is the one the RFC actually specifies. An API source that can only issue a single request is limited to
/// whatever fits in one response, which is rarely the whole table.
/// </para>
/// </summary>
public static class PipelineApiPagination
{
    /// <summary>One request. Correct for an endpoint that returns everything.</summary>
    public const string None = "none";

    /// <summary>Increment a page-number query parameter until a page comes back empty.</summary>
    public const string Page = "page";

    /// <summary>Advance a row-offset query parameter by the page size.</summary>
    public const string Offset = "offset";

    /// <summary>
    /// Read an opaque token out of the response and send it back as a query parameter. Stops when the
    /// response carries no token.
    /// </summary>
    public const string Cursor = "cursor";

    /// <summary>Follow <c>rel="next"</c> in the RFC-5988 <c>Link</c> response header.</summary>
    public const string LinkHeader = "link";

    public static readonly string[] All = [None, Page, Offset, Cursor, LinkHeader];

    /// <summary>True for the styles driven by a query parameter this side controls.</summary>
    public static bool UsesPageSize(string? mode) => mode is Page or Offset;
}

/// <summary>How a <c>destination.api</c> step packages rows into request bodies.</summary>
public static class PipelineApiWriteShapes
{
    /// <summary>
    /// One request per batch, body is a JSON array of row objects (optionally wrapped in a named property).
    /// The efficient choice, and what most bulk endpoints accept.
    /// </summary>
    public const string Batch = "batch";

    /// <summary>
    /// One request per row, body is a single JSON object. Needed for APIs that accept only one record per
    /// call, and it reports failures per row — at the cost of one round trip each.
    /// </summary>
    public const string Row = "row";

    public static readonly string[] All = [Batch, Row];
}

/// <summary>
/// The file a <c>destination.email</c> step attaches. Stored strings, so a contract.
/// <para>
/// Note which formats are <em>absent</em>: Parquet is not here, and neither is anything else DuckDB can
/// COPY to. An emailed file has to be openable by whoever receives it, and a recipient who can open Parquet
/// would rather have a dataset grant than an attachment.
/// </para>
/// </summary>
public static class PipelineExportFormats
{
    /// <summary>Delimited text. The delimiter is a separate field, so this covers TSV and semicolon files.</summary>
    public const string Csv = "csv";

    /// <summary>
    /// A real workbook, written by ClosedXML rather than by DuckDB. DuckDB's xlsx writer lives in the
    /// <c>excel</c> extension, and that extension cannot be relied on: <c>INSTALL excel</c> downloads over
    /// plain HTTP, gets a 307 to the CDN and does not follow it, so on a machine with no pre-seeded
    /// extension directory it simply fails. An export format that works on one server and not another is
    /// worse than a dependency.
    /// </summary>
    public const string Xlsx = "xlsx";

    /// <summary>A JSON array of row objects.</summary>
    public const string Json = "json";

    public static readonly string[] All = [Csv, Xlsx, Json];

    /// <summary>The extension, without the dot. Also what the default file name is built from.</summary>
    public static string Extension(string? format) => format switch
    {
        Xlsx => "xlsx",
        Json => "json",
        _ => "csv"
    };

    public static string ContentType(string? format) => format switch
    {
        Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        Json => "application/json",
        _ => "text/csv"
    };
}

/// <summary>
/// What a <c>destination.email</c> step does when the run produced no rows.
/// <para>
/// Configurable rather than fixed, because the three answers are genuinely different intentions and no
/// default is right for all of them: "send it" proves the schedule ran, "skip it" keeps a quiet mailbox
/// quiet, and "fail" treats an empty result as the anomaly it sometimes is.
/// </para>
/// </summary>
public static class PipelineEmailEmptyBehaviour
{
    /// <summary>Send the mail with an empty file (header row only). The default.</summary>
    public const string Send = "send";

    /// <summary>Send nothing and succeed. The step records zero rows, so the run still shows what happened.</summary>
    public const string Skip = "skip";

    /// <summary>Fail the step.</summary>
    public const string Fail = "fail";

    public static readonly string[] All = [Send, Skip, Fail];
}

/// <summary>
/// What a <c>destination.email</c> step does when the export is too large to attach.
/// <para>
/// There is a real ceiling here and it is not ours to raise: the mail goes out through Resend, whose limit
/// is on the assembled message, and the attachment travels to it base64-encoded inside a JSON body — about
/// a third larger than the file on disk. So this is not a tuning knob that can be turned up until it works.
/// </para>
/// </summary>
public static class PipelineEmailOversizeBehaviour
{
    /// <summary>Fail the step, naming the size. The default.</summary>
    public const string Fail = "fail";

    /// <summary>
    /// Write the rows into a dataset table instead and email a link to it. Uses the same write path as
    /// <c>destination.dataset</c> — no new download endpoint, and the recipient's existing dataset
    /// permissions still apply, which an emailed file bypasses entirely.
    /// </summary>
    public const string DatasetLink = "link";

    public static readonly string[] All = [Fail, DatasetLink];
}

/// <summary>
/// Request body encodings an API step can send.
/// <para>
/// The source and the destination do NOT offer the same list, and the asymmetry is the point. A
/// <c>source.api</c> body is text the author typed, so the content type is only a header and anything is
/// legitimate. A <c>destination.api</c> body is <em>generated from rows</em>, so the only content types on
/// offer are the ones this app can actually serialize rows into. Offering XML on the destination would be a
/// promise with no serializer behind it.
/// </para>
/// </summary>
public static class PipelineApiContentTypes
{
    public const string Json = "application/json";

    /// <summary>
    /// <c>a=1&amp;b=2</c>. Cannot express an array, which is why a batching destination refuses it — see
    /// <see cref="SupportsBatch"/>.
    /// </summary>
    public const string Form = "application/x-www-form-urlencoded";

    public const string Text = "text/plain";
    public const string Xml = "application/xml";
    public const string Ndjson = "application/x-ndjson";

    /// <summary>Everything a source may declare. The source never generates the body, so this is a hint list.</summary>
    public static readonly string[] All = [Json, Form, Text, Xml, Ndjson];

    /// <summary>The content types a destination can actually build a body for.</summary>
    public static readonly string[] Writable = [Json, Form];

    /// <summary>
    /// Whether a batch of rows can be expressed in this encoding. False for form encoding: there is no
    /// standard way to put an array in it — <c>records[0][sku]=</c> is one framework's convention and
    /// <c>records[]=</c> is another — so guessing would produce a body the endpoint may silently misread.
    /// </summary>
    public static bool SupportsBatch(string? contentType) => contentType != Form;

    /// <summary>Normalizes to a known value, falling back to JSON. Empty config means JSON, as before.</summary>
    public static string ResolveWritable(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) || !Writable.Contains(contentType) ? Json : contentType!;
}

/// <summary>
/// What a <c>source.api</c> step does with nested JSON before it becomes columns.
/// </summary>
public static class PipelineApiFlatten
{
    /// <summary>
    /// Lift one level of nesting into <c>parent_child</c> columns; anything deeper, and any array, stays as
    /// JSON text. The default: column names stay predictable, and a response whose shape varies deep down
    /// cannot silently change the table's schema and trip drift detection on every run.
    /// </summary>
    public const string OneLevel = "one";

    /// <summary>Every scalar leaf becomes a column, at any depth.</summary>
    public const string All = "all";

    /// <summary>Top-level keys only; every object and array stays JSON text.</summary>
    public const string None = "none";

    public static readonly string[] Modes = [OneLevel, All, None];
}

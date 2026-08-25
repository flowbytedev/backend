using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Walks a paginated HTTP endpoint and lands the rows in a JSON file the DuckDB reader can ingest.
/// <para>
/// Separate from <see cref="PipelineApiClient"/> on purpose: that class knows about auth, retry and
/// redirects; this one knows about pagination shapes, JSON navigation and flattening. Keeping them apart is
/// what lets the destination writer reuse all of the former and none of the latter.
/// </para>
/// </summary>
public interface IPipelineApiReader
{
    Task<ApiFetchResult> FetchToFileAsync(ApiFetchRequest request, CancellationToken ct = default);
}

public sealed class ApiFetchRequest
{
    public required ResolvedApiCredential Credential { get; init; }
    public required string Url { get; init; }
    public string Method { get; init; } = "GET";
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? Body { get; init; }

    /// <summary>Dotted path to the array of rows, e.g. <c>data.items</c>. Empty means the root.</summary>
    public string? JsonPath { get; init; }

    public string Pagination { get; init; } = PipelineApiPagination.None;
    public string? PageParam { get; init; }
    public string? PageSizeParam { get; init; }
    public int? PageSize { get; init; }
    public int StartPage { get; init; } = 1;

    /// <summary>Where in the response body the next cursor lives, e.g. <c>meta.next_cursor</c>.</summary>
    public string? CursorPath { get; init; }

    /// <summary>Query parameter the cursor is sent back in.</summary>
    public string? CursorParam { get; init; }

    public string Flatten { get; init; } = PipelineApiFlatten.OneLevel;

    /// <summary>Stops early — this is how preview stays cheap against a large endpoint.</summary>
    public int? RowLimit { get; init; }

    public int MaxPages { get; init; } = 1000;

    public IJobProgress? Progress { get; init; }
}

public sealed class ApiFetchResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public long RowCount { get; init; }
    public int Pages { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public string? Error { get; init; }
    public string? ErrorType { get; init; }

    public static ApiFetchResult Fail(string error, string errorType) =>
        new() { Success = false, Error = error, ErrorType = errorType };
}

public class PipelineApiReader(IPipelineApiClient client) : IPipelineApiReader
{
    public async Task<ApiFetchResult> FetchToFileAsync(ApiFetchRequest request, CancellationToken ct = default)
    {
        // Pass one: stream every row to NDJSON exactly as it arrives, recording the union of keys.
        var stagePath = Path.Combine(Path.GetTempPath(), $"pl_api_{Guid.NewGuid():N}.ndjson");
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        long rows = 0;
        var pages = 0;

        try
        {
            await using (var stage = new StreamWriter(stagePath, false, new UTF8Encoding(false)))
            {
                string? nextUrl = request.Url;
                string? cursor = null;
                var page = request.StartPage;

                while (nextUrl is not null && pages < Math.Max(1, request.MaxPages))
                {
                    ct.ThrowIfCancellationRequested();

                    var query = BuildPageQuery(request, page, rows, cursor);

                    var response = await client.SendAsync(new ApiRequest
                    {
                        Credential = request.Credential,
                        Url = nextUrl,
                        Method = request.Method,
                        Headers = request.Headers,
                        Query = query,
                        Body = request.Body
                    }, ct);

                    if (!response.Success)
                        return ApiFetchResult.Fail(response.Error!, response.ErrorType ?? PipelineErrorType.ApiError);

                    pages++;

                    JsonNode? root;
                    try
                    {
                        root = JsonNode.Parse(response.Body ?? string.Empty);
                    }
                    catch (JsonException ex)
                    {
                        return ApiFetchResult.Fail(
                            $"The API returned a body that is not valid JSON: {ex.Message}",
                            PipelineErrorType.ApiError);
                    }

                    var located = Navigate(root, request.JsonPath);
                    if (located is null)
                    {
                        return ApiFetchResult.Fail(
                            string.IsNullOrWhiteSpace(request.JsonPath)
                                ? "The API returned an empty body."
                                : $"No value at '{request.JsonPath}' in the API response. "
                                  + $"The response's top level has: {DescribeShape(root)}.",
                            PipelineErrorType.ApiError);
                    }

                    var batch = AsRowList(located);
                    if (batch is null)
                    {
                        return ApiFetchResult.Fail(
                            $"'{request.JsonPath ?? "the response root"}' is a "
                            + $"{located.GetValueKind().ToString().ToLowerInvariant()}, not a list of records. "
                            + "Point the JSON path at the array of rows.",
                            PipelineErrorType.ApiError);
                    }

                    var pageRows = 0;

                    foreach (var item in batch)
                    {
                        var flat = Flatten(item, request.Flatten);

                        foreach (var key in flat.Keys)
                            if (seen.Add(key)) columns.Add(key);

                        await stage.WriteLineAsync(Serialize(flat));
                        rows++;
                        pageRows++;

                        if (request.RowLimit is int limit && rows >= limit) break;
                    }

                    request.Progress?.WriteLine(
                        $"      page {pages}: {pageRows:N0} rows from {response.SafeUrl}");

                    if (request.RowLimit is int cap && rows >= cap) break;

                    // ---- advance ----

                    if (request.Pagination == PipelineApiPagination.None) break;

                    // An empty page is the end for every style: no rows means nothing further to ask for.
                    if (pageRows == 0) break;

                    switch (request.Pagination)
                    {
                        case PipelineApiPagination.Page:
                        case PipelineApiPagination.Offset:
                            page++;
                            // A page smaller than requested is the last one. Saves a guaranteed-empty
                            // round trip on every single run.
                            if (request.PageSize is int size && size > 0 && pageRows < size) nextUrl = null;
                            break;

                        case PipelineApiPagination.Cursor:
                            var next = Navigate(root, request.CursorPath)?.ToString();
                            if (string.IsNullOrWhiteSpace(next) || next == cursor) nextUrl = null;
                            else cursor = next;
                            break;

                        case PipelineApiPagination.LinkHeader:
                            nextUrl = string.IsNullOrWhiteSpace(response.NextLink) ? null : response.NextLink;
                            break;
                    }
                }

                if (pages >= Math.Max(1, request.MaxPages) && nextUrl is not null)
                {
                    request.Progress?.WriteLine(
                        $"      stopped at the {request.MaxPages}-page ceiling (Pipelines:ApiMaxPages) — "
                        + "the data may be incomplete.");
                }
            }

            // Pass two: normalize. Every row gets every column, so a response that omits null fields — very
            // common — cannot produce a relation whose columns depend on which page happened to be sampled.
            var finalPath = Path.Combine(Path.GetTempPath(), $"pl_api_{Guid.NewGuid():N}.json");
            await NormalizeAsync(stagePath, finalPath, columns, ct);

            return new ApiFetchResult
            {
                Success = true,
                FilePath = finalPath,
                RowCount = rows,
                Pages = pages,
                Columns = columns
            };
        }
        finally
        {
            TryDelete(stagePath);
        }
    }

    // ------------------------------------------------------------------ pagination

    private static Dictionary<string, string>? BuildPageQuery(
        ApiFetchRequest request, int page, long rowsSoFar, string? cursor)
    {
        // The Link header carries the whole next URL, including its own paging params — adding ours would
        // fight it.
        if (request.Pagination is PipelineApiPagination.None or PipelineApiPagination.LinkHeader)
            return null;

        var query = new Dictionary<string, string>(StringComparer.Ordinal);

        if (request.Pagination == PipelineApiPagination.Cursor)
        {
            if (!string.IsNullOrWhiteSpace(cursor) && !string.IsNullOrWhiteSpace(request.CursorParam))
                query[request.CursorParam!] = cursor!;
        }
        else
        {
            var param = request.PageParam
                        ?? (request.Pagination == PipelineApiPagination.Offset ? "offset" : "page");

            query[param] = request.Pagination == PipelineApiPagination.Offset
                ? rowsSoFar.ToString(CultureInfo.InvariantCulture)
                : page.ToString(CultureInfo.InvariantCulture);
        }

        if (request.PageSize is int size && size > 0 && !string.IsNullOrWhiteSpace(request.PageSizeParam))
            query[request.PageSizeParam!] = size.ToString(CultureInfo.InvariantCulture);

        return query.Count == 0 ? null : query;
    }

    // ------------------------------------------------------------------ JSON shaping

    /// <summary>
    /// Walks a dotted path, with <c>[n]</c> indices: <c>data.items</c>, <c>result[0].rows</c>. Not JSONPath —
    /// deliberately, because a filter expression here would be a query language nobody asked for and the
    /// visual editor could not author.
    /// </summary>
    internal static JsonNode? Navigate(JsonNode? node, string? path)
    {
        if (node is null) return null;
        if (string.IsNullOrWhiteSpace(path)) return node;

        var current = node;

        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Trim();

            while (segment.Length > 0)
            {
                var bracket = segment.IndexOf('[');

                if (bracket != 0)
                {
                    var name = bracket < 0 ? segment : segment[..bracket];
                    if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out current))
                        return null;

                    segment = bracket < 0 ? string.Empty : segment[bracket..];
                    continue;
                }

                var close = segment.IndexOf(']');
                if (close < 0) return null;

                if (!int.TryParse(segment[1..close], out var index)) return null;
                if (current is not JsonArray array || index < 0 || index >= array.Count) return null;

                current = array[index];
                segment = segment[(close + 1)..];
            }

            if (current is null) return null;
        }

        return current;
    }

    /// <summary>
    /// The rows in a located node. An array is the list; a single object is a one-row result, which is what
    /// a "get one record" endpoint returns and is worth accepting rather than calling an error.
    /// </summary>
    private static List<JsonNode?>? AsRowList(JsonNode node) => node switch
    {
        JsonArray array => array.ToList(),
        JsonObject obj => [obj],
        _ => null
    };

    /// <summary>
    /// Turns one record into flat column values. Deeper structure is kept as JSON text rather than dropped,
    /// so nothing is silently lost — a <c>transform.sql</c> step can still pick it apart.
    /// </summary>
    internal static Dictionary<string, JsonNode?> Flatten(JsonNode? item, string mode)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        if (item is not JsonObject obj)
        {
            // A list of scalars is legitimate — ["a","b"] becomes one column called value.
            result["value"] = item?.DeepClone();
            return result;
        }

        foreach (var (key, value) in obj)
        {
            switch (value)
            {
                case JsonObject nested when mode != PipelineApiFlatten.None:
                    if (mode == PipelineApiFlatten.All)
                    {
                        foreach (var (childKey, childValue) in Flatten(nested, mode))
                            result[$"{key}_{childKey}"] = childValue;
                    }
                    else
                    {
                        // One level: lift the nested object's scalars, keep anything deeper as JSON.
                        foreach (var (childKey, childValue) in nested)
                            result[$"{key}_{childKey}"] = childValue is JsonObject or JsonArray
                                ? JsonValue.Create(childValue.ToJsonString())
                                : childValue?.DeepClone();
                    }
                    break;

                case JsonObject or JsonArray:
                    // Arrays are never flattened at any mode: doing so would turn one record into many
                    // columns whose count depends on the data, and a pipeline's columns must not.
                    result[key] = JsonValue.Create(value.ToJsonString());
                    break;

                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }

        return result;
    }

    private static string Serialize(Dictionary<string, JsonNode?> row)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in row) obj[key] = value?.DeepClone();
        return obj.ToJsonString();
    }

    /// <summary>
    /// Rewrites the staged NDJSON as a JSON array in which every object carries every discovered key, with
    /// explicit nulls for the gaps.
    /// </summary>
    private static async Task NormalizeAsync(
        string stagePath, string finalPath, List<string> columns, CancellationToken ct)
    {
        await using var output = new StreamWriter(finalPath, false, new UTF8Encoding(false));
        using var input = new StreamReader(stagePath);

        await output.WriteAsync('[');

        var first = true;
        string? line;

        while ((line = await input.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;

            var parsed = JsonNode.Parse(line) as JsonObject;
            var row = new JsonObject();

            foreach (var column in columns)
                row[column] = parsed is not null && parsed.TryGetPropertyValue(column, out var v)
                    ? v?.DeepClone()
                    : null;

            if (!first) await output.WriteAsync(',');
            await output.WriteAsync(row.ToJsonString());
            first = false;
        }

        await output.WriteAsync(']');
    }

    private static string DescribeShape(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Count == 0
            ? "no properties"
            : string.Join(", ", obj.Select(p => p.Key).Take(12)),
        JsonArray array => $"an array of {array.Count}",
        null => "nothing",
        _ => node.GetValueKind().ToString().ToLowerInvariant()
    };

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

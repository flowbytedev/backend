using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Sends a pipeline's rows to an HTTP endpoint. The mirror of <see cref="IExternalTableWriter"/>: same shape,
/// same contract that errors come back rather than being thrown, and the same refusal to write without a
/// credential that explicitly permits it.
/// <para>
/// <b>This destination has no transaction, and cannot pretend otherwise.</b> A dataset write is atomic —
/// <c>PromoteRelationAsync</c> either lands every row or none. An API write is N independent requests, so a
/// failure at request 40 of 100 leaves 39 batches delivered and no way to recall them. Everything below
/// follows from that: the failure message names how many rows are already through, and the write modes are
/// deliberately not the dataset ones.
/// </para>
/// </summary>
public interface IPipelineApiWriter
{
    Task<ApiWriteResult> WriteAsync(ApiWriteRequest request, CancellationToken ct = default);
}

public sealed class ApiWriteRequest
{
    /// <summary>Credential name or id. Must have <see cref="ApiCredential.AllowWrite"/>.</summary>
    public required string CredentialReference { get; init; }
    public required string CompanyId { get; init; }

    /// <summary>The scratch dataset and relation holding the rows to send.</summary>
    public required string SourceDatasetId { get; init; }
    public required string SourceRelation { get; init; }

    public required string Url { get; init; }
    public string Method { get; init; } = "POST";
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>One of <see cref="PipelineApiWriteShapes"/>.</summary>
    public string Shape { get; init; } = PipelineApiWriteShapes.Batch;

    /// <summary>
    /// How the rows are encoded into the body — one of <see cref="PipelineApiContentTypes.Writable"/>.
    /// <para>
    /// Only the encodings this class can actually produce are offered. Form encoding additionally forces
    /// one request per row, because there is no standard way to put an array into it.
    /// </para>
    /// </summary>
    public string ContentType { get; init; } = PipelineApiContentTypes.Json;

    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Wraps a batch array in a named property — <c>{"records": [...]}</c> rather than a bare array. Empty
    /// sends the array itself.
    /// </summary>
    public string? BodyProperty { get; init; }

    /// <summary>
    /// A JSON object sent around the rows, with <see cref="BodyProperty"/> naming where inside it they go —
    /// <c>{"publishTime": "…", "value": [...]}</c>. Already token-substituted by the engine.
    /// <para>
    /// This exists because the rows cannot express the whole body. An endpoint that wants a publish time or
    /// a control number wants one <em>per request</em>, and no column carries it: putting it in the rows
    /// would repeat it on every record, which is a different document.
    /// </para>
    /// </summary>
    public string? Envelope { get; init; }

    /// <summary>
    /// Stop at the first failed request. On by default: continuing means deliberately sending more rows to
    /// an endpoint that has already said no, which for a 401 or a 400 is just noise.
    /// </summary>
    public bool StopOnError { get; init; } = true;

    public IJobProgress? Progress { get; init; }
}

public sealed class ApiWriteResult
{
    public bool Success { get; init; }
    public long RowsSent { get; init; }
    public int RequestsSent { get; init; }
    public int RequestsFailed { get; init; }
    public string? Error { get; init; }
    public string? ErrorType { get; init; }

    public static ApiWriteResult Fail(string error, string errorType, long rowsSent = 0) =>
        new() { Success = false, Error = error, ErrorType = errorType, RowsSent = rowsSent };
}

// No PipelineOptions dependency: the batch size arrives on the request, decided by the engine from the
// node's config with the server default already applied. Taking options here as well would be a second
// source for one number.
public class PipelineApiWriter(
    IPipelineApiClient client,
    IPipelineStore store) : IPipelineApiWriter
{
    public async Task<ApiWriteResult> WriteAsync(ApiWriteRequest request, CancellationToken ct = default)
    {
        var credential = await client.ResolveAsync(
            request.CredentialReference, request.CompanyId, forWrite: true, ct);

        if (credential is null)
            return ApiWriteResult.Fail(
                $"No API credential called '{request.CredentialReference}' is available to this company.",
                PipelineErrorType.Invalid);

        if (!credential.Credential.IsEnabled)
            return ApiWriteResult.Fail(
                $"The API credential '{credential.Name}' is disabled.", PipelineErrorType.NotWritable);

        // The same rule DatabaseAdminService.LoadAdminConnectionAsync follows: refuse, never escalate.
        if (!credential.Credential.AllowWrite)
            return ApiWriteResult.Fail(
                $"The API credential '{credential.Name}' is not allowed to send data. Turn on "
                + "\"May send data\" on the credential if this endpoint is meant to be written to.",
                PipelineErrorType.NotWritable);

        // Form encoding cannot express an array, so it is one row per request whatever the shape says. The
        // compiler rejects that combination at save time; this is the run-time backstop, because a graph can
        // be edited into an invalid state and a silently mis-encoded body is worse than a slow one.
        var batchSize = request.Shape == PipelineApiWriteShapes.Row
                        || !PipelineApiContentTypes.SupportsBatch(request.ContentType)
            ? 1
            : Math.Max(1, request.BatchSize);

        // Parsed once, before a single row is read, and returned as a failure rather than thrown. A
        // malformed envelope is a config error, and discovering it at request 40 of 100 would leave 39
        // batches delivered under a body that was never going to be right.
        JsonObject? envelope = null;

        if (!string.IsNullOrWhiteSpace(request.Envelope))
        {
            var (parsed, error) = ParseEnvelope(request.Envelope!, request.BodyProperty);
            if (error is not null) return ApiWriteResult.Fail(error, PipelineErrorType.Invalid);

            envelope = parsed;
        }

        return await store.ReadRelationAsync(
            request.SourceDatasetId, request.SourceRelation,
            async (reader, columns, token) =>
                await PumpAsync(request, credential, envelope, reader, columns, batchSize, token),
            ct);
    }

    /// <summary>
    /// The envelope template as a <see cref="JsonObject"/>, or a message naming what is wrong with it.
    /// <para>
    /// A missing <paramref name="bodyProperty"/> is refused rather than defaulted, and it is the one case
    /// here that could not go either way: an object has no natural place for an array, so inventing a name
    /// would send a body the endpoint accepts and quietly ignores — the worst of the failures available.
    /// </para>
    /// </summary>
    internal static (JsonObject? Envelope, string? Error) ParseEnvelope(string text, string? bodyProperty)
    {
        if (string.IsNullOrWhiteSpace(bodyProperty))
        {
            return (null, "\"Extra body fields\" also needs \"Wrap the batch in\" - it names the property "
                          + "inside that object where the rows go.");
        }

        JsonNode? node;

        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            // Named as a config problem rather than reported as a parser position, because by far the most
            // common cause is a token that expanded into an unquoted value.
            return (null, $"\"Extra body fields\" is not valid JSON: {ex.Message}");
        }

        if (node is not JsonObject obj)
        {
            return (null, "\"Extra body fields\" has to be a JSON object - { \"publishTime\": \"...\" } - "
                          + "since the rows are added to it as a property.");
        }

        return (obj, null);
    }

    private async Task<ApiWriteResult> PumpAsync(
        ApiWriteRequest request,
        ResolvedApiCredential credential,
        JsonObject? envelope,
        DbDataReader reader,
        List<PipelineColumn> columns,
        int batchSize,
        CancellationToken ct)
    {
        var names = columns.Select(c => c.Name).ToArray();

        long rowsSent = 0;
        var requests = 0;
        var failures = 0;
        string? firstError = null;
        string? firstErrorType = null;

        var buffer = new List<JsonObject>(batchSize);

        async Task<bool> FlushAsync()
        {
            if (buffer.Count == 0) return true;

            var body = BuildBody(request, envelope, buffer);

            var response = await client.SendAsync(new ApiRequest
            {
                Credential = credential,
                Url = request.Url,
                Method = request.Method,
                Headers = request.Headers,
                Body = body,
                ContentType = request.ContentType
            }, ct);

            requests++;

            if (response.Success)
            {
                rowsSent += buffer.Count;
                buffer.Clear();
                return true;
            }

            failures++;
            firstError ??= response.Error;
            firstErrorType ??= response.ErrorType;
            buffer.Clear();
            return !request.StopOnError;
        }

        while (await reader.ReadAsync(ct))
        {
            var row = new JsonObject();

            for (var i = 0; i < names.Length; i++)
                row[names[i]] = ToJson(reader.IsDBNull(i) ? null : reader.GetValue(i));

            buffer.Add(row);

            if (buffer.Count < batchSize) continue;

            if (!await FlushAsync()) break;

            if (requests % 20 == 0)
                request.Progress?.WriteLine($"      sent {rowsSent:N0} rows in {requests:N0} requests");
        }

        // Anything left after the loop, unless we are already bailing out.
        if (firstError is null || !request.StopOnError) await FlushAsync();

        if (firstError is not null)
        {
            // Naming the delivered count is the important part: this write is not atomic, so "it failed"
            // without a number leaves no way to know what the endpoint already has.
            var sent = rowsSent > 0
                ? $" {rowsSent:N0} row(s) were already accepted in {requests - failures:N0} request(s) and "
                  + "cannot be recalled."
                : " No rows were accepted.";

            return new ApiWriteResult
            {
                Success = false,
                RowsSent = rowsSent,
                RequestsSent = requests,
                RequestsFailed = failures,
                Error = firstError + sent,
                ErrorType = firstErrorType ?? PipelineErrorType.ApiError
            };
        }

        return new ApiWriteResult
        {
            Success = true,
            RowsSent = rowsSent,
            RequestsSent = requests,
            RequestsFailed = failures
        };
    }

    private static string BuildBody(ApiWriteRequest request, JsonObject? envelope, List<JsonObject> buffer)
    {
        // Form encoding is single-row by construction — see the batchSize note in WriteAsync. It carries no
        // envelope either: a flat key=value body cannot hold one, and the compiler blocks the combination.
        if (request.ContentType == PipelineApiContentTypes.Form)
            return FormEncode(buffer[0], request.BodyProperty);

        // What this step was going to send: an array for a batch, the object itself for one-per-row. The
        // envelope wraps whichever of those it is rather than always an array, because a step set to one
        // request per row means one record, and {"value": [one]} is a different document.
        JsonNode payload;

        if (request.Shape == PipelineApiWriteShapes.Row)
        {
            payload = buffer[0].DeepClone();
        }
        else
        {
            var array = new JsonArray();
            foreach (var row in buffer) array.Add(row.DeepClone());
            payload = array;
        }

        if (envelope is not null)
        {
            // Cloned per request: a JsonNode has exactly one parent, so assigning the payload into the
            // shared template would detach it again on the next request. The clone copies the template — a
            // handful of scalars — never the rows.
            var body = (JsonObject)envelope.DeepClone();
            body[request.BodyProperty!] = payload;
            return body.ToJsonString();
        }

        // No envelope: unchanged behaviour. In particular one-per-row still sends the bare object even when
        // bodyProperty is set, because wrapping it now would rewrite the body of every step already doing it.
        if (request.Shape == PipelineApiWriteShapes.Row || string.IsNullOrWhiteSpace(request.BodyProperty))
            return payload.ToJsonString();

        return new JsonObject { [request.BodyProperty!] = payload }.ToJsonString();
    }

    /// <summary>
    /// One row as <c>a=1&amp;b=2</c>, percent-encoded.
    /// <para>
    /// Two choices here are worth naming. A <b>null becomes an empty value</b> rather than being omitted:
    /// form encoding has no null, and an HTML form submits an empty field rather than dropping it, so this
    /// keeps the key set stable across rows — an endpoint that positionally trusts the fields would otherwise
    /// see a different shape per row. And <c>Uri.EscapeDataString</c> is used, then <c>%20</c> is rewritten
    /// to <c>+</c>, because that is what <c>application/x-www-form-urlencoded</c> specifies for a space and
    /// EscapeDataString does not do it.
    /// </para>
    /// <para>
    /// <paramref name="bodyProperty"/> prefixes every key — <c>record[sku]=…</c> — for endpoints that expect
    /// a named group. Empty sends bare keys.
    /// </para>
    /// </summary>
    internal static string FormEncode(JsonObject row, string? bodyProperty)
    {
        var parts = new List<string>(row.Count);

        foreach (var (name, value) in row)
        {
            var key = string.IsNullOrWhiteSpace(bodyProperty)
                ? name
                : $"{bodyProperty}[{name}]";

            parts.Add($"{Encode(key)}={Encode(Scalar(value))}");
        }

        return string.Join("&", parts);
    }

    /// <summary>
    /// A JSON value as the text that goes into a form field. An object or array is kept as JSON text — form
    /// encoding has no nesting, and dropping the column would lose data silently.
    /// </summary>
    private static string Scalar(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => value.ToJsonString().Trim('"')
    };

    private static string Encode(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+");

    /// <summary>
    /// DuckDB value to JSON. Dates and times go out as ISO-8601 strings rather than whatever the invariant
    /// culture produces: <c>DateOnly.ToString()</c> is <c>MM/dd/yyyy</c>, which almost every API rejects and
    /// a few silently misread as a different date.
    /// </summary>
    internal static JsonNode? ToJson(object? value) => value switch
    {
        null or DBNull => null,
        bool b => JsonValue.Create(b),
        byte or sbyte or short or ushort or int or uint or long or ulong
            => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        float or double => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        decimal d => JsonValue.Create(d),
        DateOnly date => JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly time => JsonValue.Create(time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString()),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString())
    };
}

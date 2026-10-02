using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Application.Shared.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Application.Shared.Services;

/// <summary>
/// Everything that talks to a MongoDB server. MongoDB has no ADO.NET driver and no SQL, so it cannot go
/// through <see cref="ExternalConnectionFactory"/>; this is its equivalent, the way the ClickHouse HTTP helpers
/// in <see cref="DatabaseTableService"/> are ClickHouse's.
/// <para>
/// <b>The connection is a URI, stored where the password normally goes.</b> A MongoDB URI carries the
/// credentials, the replica set, <c>authSource</c>, TLS and the <c>+srv</c> lookup, which the host/port/user
/// fields cannot express. So <see cref="DatabaseConnection.SecretEncrypted"/> holds the whole URI, encrypted,
/// and <see cref="DatabaseConnection.Host"/> keeps only a credential-free description for display. Callers
/// decrypt in place (the same naming trap as everywhere else), so by the time anything here runs
/// <c>SecretEncrypted</c> holds the plaintext URI.
/// </para>
/// <para>
/// <b>Read-only by construction, not by session setting.</b> MongoDB has no read-only session mode, so the
/// guarantee comes from what is issued: only <c>ping</c>, <c>listCollections</c> and <c>aggregate</c>, and an
/// aggregate is refused if it contains <c>$out</c> or <c>$merge</c>, the two stages that write.
/// </para>
/// </summary>
internal static class MongoSource
{
    public const int DefaultPort = 27017;

    /// <summary>
    /// The one format DuckDB's JSON reader detects as TIMESTAMP — verified on DuckDB.NET 1.3: an ISO value
    /// with a <c>T</c>, a <c>Z</c>, an offset or seven fractional digits all stay VARCHAR. BSON dates are UTC at
    /// millisecond precision, so this loses nothing.
    /// </summary>
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    // MongoClient owns a connection pool and is meant to be shared for the life of the process, so one per
    // distinct URI. Keyed by the plaintext URI: rotating a password produces a new key, and the old client
    // is simply never used again.
    private static readonly ConcurrentDictionary<string, MongoClient> Clients = new();

    // ------------------------------------------------------------------ connection

    /// <summary>Validates a URI, with a message that says what was wrong rather than the driver's.</summary>
    public static MongoUrl ParseUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("A MongoDB connection URI is required (mongodb://… or mongodb+srv://…).");

        try
        {
            return MongoUrl.Create(uri.Trim());
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"That is not a valid MongoDB connection URI: {ex.Message}");
        }
    }

    /// <summary>
    /// What gets stored in the plain columns: hosts, port, user and TLS, never the password. Lets the
    /// connections list show where a MongoDB connection points without decrypting anything.
    /// </summary>
    public static (string Host, int Port, string? Username, bool UseTls, string? Database) Describe(MongoUrl url)
    {
        var srv = url.Scheme == MongoDB.Driver.Core.Configuration.ConnectionStringScheme.MongoDBPlusSrv;
        var servers = url.Servers?.ToList() ?? new List<MongoServerAddress>();

        var host = srv || servers.Count <= 1
            ? (servers.FirstOrDefault()?.Host ?? string.Empty)
            : string.Join(",", servers.Select(s => s.Port == DefaultPort ? s.Host : $"{s.Host}:{s.Port}"));

        if (host.Length > 500) host = host[..500];

        var port = srv ? DefaultPort : servers.FirstOrDefault()?.Port ?? DefaultPort;
        return (host, port, url.Username, url.UseTls || srv, url.DatabaseName);
    }

    private static MongoClient Client(DatabaseConnection c)
    {
        var uri = c.SecretEncrypted;
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidOperationException("This MongoDB connection has no URI saved. Edit the connection and paste one.");

        return Clients.GetOrAdd(uri.Trim(), key =>
        {
            var settings = MongoClientSettings.FromUrl(ParseUri(key));

            // The driver waits 30s by default to find a server, which is how long a mistyped host would hang the
            // Test button. The SQL engines here give up after 15. Only applied when the URI does not set it.
            if (!key.Contains("serverSelectionTimeoutMS", StringComparison.OrdinalIgnoreCase))
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
            if (!key.Contains("connectTimeoutMS", StringComparison.OrdinalIgnoreCase))
                settings.ConnectTimeout = TimeSpan.FromSeconds(15);

            settings.ApplicationName ??= "flowbyte";
            return new MongoClient(settings);
        });
    }

    /// <summary>
    /// The database to read: an explicit override (a step's schema field), else the connection's Database, else
    /// the one named in the URI's path.
    /// </summary>
    private static IMongoDatabase Database(DatabaseConnection c, string? databaseOverride)
    {
        var name = !string.IsNullOrWhiteSpace(databaseOverride) ? databaseOverride.Trim()
            : !string.IsNullOrWhiteSpace(c.DatabaseName) ? c.DatabaseName!.Trim()
            : ParseUri(c.SecretEncrypted).DatabaseName;

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "No MongoDB database is named. Set Database on the connection, or put it in the URI path (mongodb://host/mydb).");

        return Client(c).GetDatabase(name);
    }

    // ------------------------------------------------------------------ probes

    public static async Task PingAsync(DatabaseConnection c, CancellationToken ct)
    {
        await Database(c, null).RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
    }

    /// <summary>
    /// Collections and views of the connection's database, as (database, collection) so they line up with the
    /// (schema, table) pairs the SQL engines return. <c>authorizedCollections</c> lets a user without the
    /// <c>listCollections</c> privilege still see the collections they can read, instead of failing.
    /// </summary>
    public static async Task<List<(string Schema, string Name)>> ListCollectionsAsync(
        DatabaseConnection c, CancellationToken ct, string? databaseOverride = null)
    {
        var database = Database(c, databaseOverride);
        var options = new ListCollectionNamesOptions { AuthorizedCollections = true };

        using var cursor = await database.ListCollectionNamesAsync(options, ct);
        var names = await cursor.ToListAsync(ct);

        return names
            .Where(n => !n.StartsWith("system.", StringComparison.Ordinal))
            .Select(n => (database.DatabaseNamespace.DatabaseName, n))
            .ToList();
    }

    // ------------------------------------------------------------------ reads

    /// <summary>
    /// Streams an aggregate's output to <paramref name="path"/> as newline-delimited JSON, one document per line,
    /// for DuckDB's <c>read_json_auto</c>. Nested documents and arrays stay nested, so they arrive as STRUCT and
    /// LIST columns. Memory stays flat: the cursor is consumed batch by batch and nothing is buffered.
    /// </summary>
    public static async Task<long> ReadToJsonLinesAsync(
        DatabaseConnection c, MongoReadRequest request, string path,
        IProgress<long>? rowProgress, CancellationToken ct)
    {
        var collection = Database(c, request.Database).GetCollection<BsonDocument>(request.Collection);

        var options = new AggregateOptions
        {
            // A $sort or $group over a large collection otherwise fails at the 100 MB stage memory limit.
            AllowDiskUse = true,
            BatchSize = request.BatchSize is > 0 ? request.BatchSize : null,
            MaxTime = request.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(request.TimeoutSeconds.Value) : null
        };

        using var cursor = await collection.AggregateAsync(
            PipelineDefinition<BsonDocument, BsonDocument>.Create(request.Pipeline), options, ct);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            // Non-ASCII text (Arabic names, for one) is written as itself rather than \uXXXX escapes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        var count = 0L;
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var document in cursor.Current)
            {
                WriteValue(json, document);
                await json.FlushAsync(ct);
                stream.WriteByte((byte)'\n');
                json.Reset(stream);

                count++;
                if (count % 5_000 == 0) rowProgress?.Report(count);
            }
        }

        rowProgress?.Report(count);
        return count;
    }

    /// <summary>
    /// The largest value of <paramref name="field"/> over exactly the documents <paramref name="request"/>
    /// would return — the incremental ceiling. Null when no document has a non-null value. <c>$max</c> skips
    /// null and missing fields, which matches SQL's <c>MAX</c>.
    /// </summary>
    public static async Task<BsonValue?> MaxAsync(
        DatabaseConnection c, MongoReadRequest request, string field, CancellationToken ct)
    {
        var pipeline = request.Pipeline.ToList();
        pipeline.Add(new BsonDocument("$group", new BsonDocument
        {
            { "_id", BsonNull.Value },
            { "m", new BsonDocument("$max", "$" + field) }
        }));

        var collection = Database(c, request.Database).GetCollection<BsonDocument>(request.Collection);
        var options = new AggregateOptions
        {
            AllowDiskUse = true,
            MaxTime = request.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(request.TimeoutSeconds.Value) : null
        };

        using var cursor = await collection.AggregateAsync(
            PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline), options, ct);
        var row = await cursor.FirstOrDefaultAsync(ct);

        var value = row?.GetValue("m", BsonNull.Value);
        return value is null || value.IsBsonNull ? null : value;
    }

    // ------------------------------------------------------------------ watermark values

    /// <summary>
    /// Text form of a BSON value for the stored watermark. Dates use the same format the JSON writer does, so
    /// the watermark reads exactly like the column it came from.
    /// </summary>
    public static string? Portable(BsonValue? value) => value?.BsonType switch
    {
        null or BsonType.Null => null,
        BsonType.DateTime => value.AsBsonDateTime.IsValidDateTime
            ? value.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture)
            : value.AsBsonDateTime.MillisecondsSinceEpoch.ToString(CultureInfo.InvariantCulture),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonType.Double => value.AsDouble.ToString("R", CultureInfo.InvariantCulture),
        BsonType.Decimal128 => value.AsDecimal128.ToString(),
        BsonType.Timestamp => value.AsBsonTimestamp.Value.ToString(CultureInfo.InvariantCulture),
        BsonType.String => value.AsString,
        _ => value.ToString()
    };

    /// <summary>
    /// Turns stored watermark text back into a BSON value of the same type as this run's ceiling, so the window
    /// compares a date with a date. MongoDB orders values of different types by type before value, so comparing
    /// a date field with the string <c>"2026-01-01"</c> would silently match nothing (or everything).
    /// Returns null when the text cannot be read as that type.
    /// </summary>
    public static BsonValue? Typed(string text, BsonType type)
    {
        var inv = CultureInfo.InvariantCulture;
        text = text.Trim();

        switch (type)
        {
            case BsonType.DateTime:
                if (DateTime.TryParse(text, inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                    return new BsonDateTime(dt);
                return long.TryParse(text, NumberStyles.Integer, inv, out var ms) ? new BsonDateTime(ms) : null;

            case BsonType.ObjectId:
                return ObjectId.TryParse(text, out var oid) ? oid : null;

            case BsonType.Int32:
            case BsonType.Int64:
                if (long.TryParse(text, NumberStyles.Integer, inv, out var l)) return new BsonInt64(l);
                return double.TryParse(text, NumberStyles.Float, inv, out var ld) ? new BsonDouble(ld) : null;

            case BsonType.Double:
                return double.TryParse(text, NumberStyles.Float, inv, out var d) ? new BsonDouble(d) : null;

            case BsonType.Decimal128:
                return Decimal128.TryParse(text, out var dec) ? new BsonDecimal128(dec) : null;

            case BsonType.Timestamp:
                return long.TryParse(text, NumberStyles.Integer, inv, out var ts) ? new BsonTimestamp(ts) : null;

            case BsonType.String:
                return new BsonString(text);

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ BSON -> JSON

    /// <summary>
    /// Plain JSON, not MongoDB Extended JSON. Extended JSON would turn every date into
    /// <c>{"$date": …}</c> and every id into <c>{"$oid": …}</c>, which DuckDB would read as one-field structs.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter w, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                w.WriteStartObject();
                foreach (var element in value.AsBsonDocument)
                {
                    w.WritePropertyName(element.Name);
                    WriteValue(w, element.Value);
                }
                w.WriteEndObject();
                break;

            case BsonType.Array:
                w.WriteStartArray();
                foreach (var item in value.AsBsonArray) WriteValue(w, item);
                w.WriteEndArray();
                break;

            case BsonType.String:
                w.WriteStringValue(value.AsString);
                break;

            case BsonType.Int32:
                w.WriteNumberValue(value.AsInt32);
                break;

            case BsonType.Int64:
                w.WriteNumberValue(value.AsInt64);
                break;

            case BsonType.Double:
                // NaN and Infinity are not JSON; the reader would reject the whole line.
                var d = value.AsDouble;
                if (double.IsFinite(d)) w.WriteNumberValue(d);
                else w.WriteNullValue();
                break;

            case BsonType.Decimal128:
                WriteDecimal(w, value.AsDecimal128);
                break;

            case BsonType.Boolean:
                w.WriteBooleanValue(value.AsBoolean);
                break;

            case BsonType.DateTime:
                // BSON can hold dates outside .NET's year 1-9999. Written as null rather than as text, because one
                // text value would turn the whole column from TIMESTAMP into VARCHAR.
                var bdt = value.AsBsonDateTime;
                if (bdt.IsValidDateTime)
                    w.WriteStringValue(bdt.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture));
                else
                    w.WriteNullValue();
                break;

            case BsonType.ObjectId:
                w.WriteStringValue(value.AsObjectId.ToString());
                break;

            case BsonType.Binary:
                WriteBinary(w, value.AsBsonBinaryData);
                break;

            case BsonType.Timestamp:
                // An internal replication type (oplog), but it does turn up in documents. Seconds since the epoch.
                var seconds = value.AsBsonTimestamp.Timestamp;
                w.WriteStringValue(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    .ToString(DateFormat, CultureInfo.InvariantCulture));
                break;

            case BsonType.Null:
            case BsonType.Undefined:
            case BsonType.MinKey:
            case BsonType.MaxKey:
                w.WriteNullValue();
                break;

            case BsonType.RegularExpression:
                var regex = value.AsBsonRegularExpression;
                w.WriteStringValue($"/{regex.Pattern}/{regex.Options}");
                break;

            default:
                // JavaScript, Symbol, DBPointer: text is the only faithful thing to write.
                w.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteDecimal(Utf8JsonWriter w, Decimal128 value)
    {
        if (Decimal128.IsNaN(value) || Decimal128.IsInfinity(value))
        {
            w.WriteNullValue();
            return;
        }

        // Written as the exact digits rather than through double, so 0.1 stays 0.1 in the file. DuckDB still
        // reads it as DOUBLE; a Map step with a DECIMAL cast is the way to keep it exact past that point.
        try
        {
            w.WriteRawValue(value.ToString(), skipInputValidation: false);
        }
        catch (JsonException)
        {
            w.WriteStringValue(value.ToString());
        }
    }

    private static void WriteBinary(Utf8JsonWriter w, BsonBinaryData data)
    {
        // UUIDs are the common binary value and are only useful as text. Subtype 3 is the legacy encoding; the C#
        // driver's own legacy byte order is the likeliest writer of it in a .NET shop.
        if (data.SubType == BsonBinarySubType.UuidStandard && data.Bytes.Length == 16)
        {
            w.WriteStringValue(data.ToGuid(GuidRepresentation.Standard).ToString());
            return;
        }

        if (data.SubType == BsonBinarySubType.UuidLegacy && data.Bytes.Length == 16)
        {
            w.WriteStringValue(data.ToGuid(GuidRepresentation.CSharpLegacy).ToString());
            return;
        }

        w.WriteStringValue(Convert.ToBase64String(data.Bytes));
    }
}

/// <summary>One read against a MongoDB collection: an aggregation pipeline, which a find is also expressed as.</summary>
internal sealed class MongoReadRequest
{
    /// <summary>Overrides the connection's database. Null uses the connection's.</summary>
    public string? Database { get; init; }

    public required string Collection { get; init; }

    public required IReadOnlyList<BsonDocument> Pipeline { get; init; }

    /// <summary>Documents per cursor round trip. Null leaves it to the server.</summary>
    public int? BatchSize { get; init; }

    /// <summary>Server-side time limit (<c>maxTimeMS</c>). Null means none.</summary>
    public int? TimeoutSeconds { get; init; }

    public MongoReadRequest With(IEnumerable<BsonDocument> extraStages) => new()
    {
        Database = Database,
        Collection = Collection,
        Pipeline = Pipeline.Concat(extraStages).ToList(),
        BatchSize = BatchSize,
        TimeoutSeconds = TimeoutSeconds
    };
}

using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Reads the query of a MongoDB database step, written the way it would be typed into mongosh or copied out
/// of Compass:
/// <code>
/// db.orders.aggregate([ { $match: { status: "paid" } }, { $unwind: "$items" } ])
/// db.orders.find({ status: "paid" }, { _id: 0, total: 1 }).sort({ at: -1 }).limit(100)
/// db.getCollection("order-lines").find()
/// db.getSiblingDB("archive").orders.find()
/// </code>
/// <para>
/// The collection travels inside the query text because the step has only one query field, and because that is
/// the form people already have. Argument values are parsed by the BSON driver's own JSON reader, which
/// accepts the shell's dialect: unquoted keys, single quotes, <c>ISODate(…)</c>, <c>ObjectId(…)</c>,
/// <c>NumberLong(…)</c>, <c>/regex/i</c>.
/// </para>
/// <para>
/// A find is rewritten as the equivalent aggregate, so there is one read path. The stages go in the order
/// mongosh applies cursor modifiers whatever order they are chained in — match, sort, skip, limit, then
/// projection — so a sort can still use a field the projection leaves out.
/// </para>
/// </summary>
internal static class MongoQuery
{
    /// <summary>The stages that write. Refused anywhere in a step's pipeline: a source step only reads.</summary>
    private static readonly HashSet<string> WritingStages = new(StringComparer.Ordinal) { "$out", "$merge" };

    public sealed record Plan(string Collection, string? Database, List<BsonDocument> Pipeline);

    /// <summary>Parses the query, throwing <see cref="FormatException"/> with a message meant for the step's author.</summary>
    public static Plan Parse(string text)
    {
        var s = (text ?? string.Empty).Trim().TrimEnd(';').TrimEnd();
        var pos = 0;

        Expect(s, ref pos, "db");
        string? database = null;

        // db.getSiblingDB("x") — the shell's way of reading another database on the same server.
        if (TryCall(s, ref pos, "getSiblingDB", out var siblingArgs))
        {
            database = SingleString(siblingArgs, "getSiblingDB");
        }

        string collection;
        if (TryCall(s, ref pos, "getCollection", out var collectionArgs))
        {
            collection = SingleString(collectionArgs, "getCollection");
        }
        else if (pos < s.Length && s[pos] == '[')
        {
            // db["order-lines"]
            var close = MatchingClose(s, pos);
            collection = SingleString(ParseArgs(s[(pos + 1)..close]), "db[…]");
            pos = close + 1;
        }
        else
        {
            // db.orders.find(…) / db.sales.orders.aggregate(…): a collection name may itself contain dots, so it
            // is everything up to the LAST dot before the method's opening parenthesis.
            Expect(s, ref pos, ".");
            var paren = s.IndexOf('(', pos);
            if (paren < 0) throw Error("Expected a call such as db.orders.find() or db.orders.aggregate([...]).");

            var head = s[pos..paren];
            var lastDot = head.LastIndexOf('.');
            if (lastDot <= 0) throw Error("Expected db.<collection>.find(...) or db.<collection>.aggregate([...]).");

            collection = head[..lastDot].Trim();
            pos += lastDot;
        }

        if (string.IsNullOrWhiteSpace(collection)) throw Error("The collection name is empty.");

        var pipeline = new List<BsonDocument>();

        if (TryCall(s, ref pos, "aggregate", out var aggregateArgs))
        {
            if (aggregateArgs.Count == 0 || aggregateArgs[0] is not BsonArray stages)
                throw Error("aggregate() takes an array of stages: db.orders.aggregate([ { $match: { … } } ]).");

            foreach (var stage in stages)
            {
                if (stage is not BsonDocument doc || doc.ElementCount != 1 || !doc.GetElement(0).Name.StartsWith('$'))
                    throw Error($"Each pipeline stage must be one {{ $stage: … }} document; got {stage.ToJson()}.");
                pipeline.Add(doc);
            }
            // A second argument (aggregate options) is accepted and ignored: allowDiskUse is always on, and a time
            // limit is the step's timeout field.
            while (TryCall(s, ref pos, "toArray", out _) || TryCall(s, ref pos, "pretty", out _)) { }
        }
        else if (TryCall(s, ref pos, "find", out var findArgs))
        {
            var filter = OptionalDocument(findArgs, 0, "find() filter");
            var projection = OptionalDocument(findArgs, 1, "find() projection");

            BsonDocument? sort = null;
            long? skip = null, limit = null;

            // Cursor modifiers, in any order. toArray()/pretty() are what people paste from the shell; harmless.
            while (pos < s.Length)
            {
                if (TryCall(s, ref pos, "sort", out var a)) sort = OptionalDocument(a, 0, "sort()") ?? sort;
                else if (TryCall(s, ref pos, "skip", out a)) skip = Number(a, "skip()");
                else if (TryCall(s, ref pos, "limit", out a)) limit = Number(a, "limit()");
                else if (TryCall(s, ref pos, "projection", out a) || TryCall(s, ref pos, "project", out a))
                    projection = OptionalDocument(a, 0, "projection()") ?? projection;
                else if (TryCall(s, ref pos, "toArray", out _) || TryCall(s, ref pos, "pretty", out _)) { }
                else throw Error($"Unsupported text after find(): '{Excerpt(s, pos)}'. Supported: .sort(), .skip(), .limit(), .projection().");
            }

            if (filter is { ElementCount: > 0 }) pipeline.Add(new BsonDocument("$match", filter));
            if (sort is { ElementCount: > 0 }) pipeline.Add(new BsonDocument("$sort", sort));
            if (skip is > 0) pipeline.Add(new BsonDocument("$skip", skip.Value));
            if (limit is > 0) pipeline.Add(new BsonDocument("$limit", limit.Value));
            if (projection is { ElementCount: > 0 }) pipeline.Add(new BsonDocument("$project", projection));
        }
        else
        {
            throw Error($"Expected .find(…) or .aggregate([…]) after the collection, found '{Excerpt(s, pos)}'.");
        }

        if (pos < s.Length)
            throw Error($"Unexpected text after the query: '{Excerpt(s, pos)}'.");

        EnsureReadOnly(pipeline);
        return new Plan(collection, database, pipeline);
    }

    /// <summary>Refuses <c>$out</c> and <c>$merge</c>. Applied to anything that becomes a step's pipeline.</summary>
    public static void EnsureReadOnly(IEnumerable<BsonDocument> pipeline)
    {
        foreach (var stage in pipeline)
            foreach (var element in stage)
                if (WritingStages.Contains(element.Name))
                    throw Error($"{element.Name} writes to the database and is not allowed in a source step. Use a destination step to write.");
    }

    // ------------------------------------------------------------------ scanning

    private static void Expect(string s, ref int pos, string token)
    {
        SkipSpace(s, ref pos);
        if (string.CompareOrdinal(s, pos, token, 0, token.Length) != 0)
            throw Error(pos == 0
                ? "A MongoDB query starts with db. — for example db.orders.find({ status: \"paid\" })."
                : $"Expected '{token}' at '{Excerpt(s, pos)}'.");
        pos += token.Length;
    }

    /// <summary>Consumes <c>.name( … )</c> if it is next, returning its parsed arguments.</summary>
    private static bool TryCall(string s, ref int pos, string name, out BsonArray args)
    {
        args = new BsonArray();
        var p = pos;
        SkipSpace(s, ref p);
        if (p >= s.Length || s[p] != '.') return false;
        p++;
        SkipSpace(s, ref p);
        if (string.CompareOrdinal(s, p, name, 0, name.Length) != 0) return false;
        p += name.Length;
        SkipSpace(s, ref p);
        if (p >= s.Length || s[p] != '(') return false;

        var close = MatchingClose(s, p);
        args = ParseArgs(s[(p + 1)..close]);
        pos = close + 1;
        SkipSpace(s, ref pos);
        return true;
    }

    /// <summary>
    /// Index of the bracket closing the one at <paramref name="open"/>, skipping brackets inside strings and
    /// regex literals so <c>{ name: "a)b" }</c> does not end the call early.
    /// </summary>
    private static int MatchingClose(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            var ch = s[i];
            switch (ch)
            {
                case '"' or '\'':
                    i = SkipString(s, i);
                    break;
                case '/' when IsRegexStart(s, i):
                    i = SkipString(s, i);
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        throw Error("A bracket is never closed — check the parentheses, [ ] and { } balance.");
    }

    private static int SkipString(string s, int start)
    {
        var quote = s[start];
        for (var i = start + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == quote) return i;
        }
        throw Error(quote == '/' ? "A /regex/ is never closed." : "A string is never closed.");
    }

    // A '/' starts a regex literal only where a value can start; anywhere else it would be a division, which
    // the JSON dialect does not have, so this cannot misfire on valid input.
    private static bool IsRegexStart(string s, int i)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (char.IsWhiteSpace(s[j])) continue;
            return s[j] is ':' or ',' or '[' or '(' ;
        }
        return false;
    }

    private static BsonArray ParseArgs(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner)) return new BsonArray();
        try
        {
            return BsonSerializer.Deserialize<BsonArray>("[" + inner + "]");
        }
        catch (Exception ex)
        {
            throw Error($"Could not read the arguments: {ex.Message}");
        }
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    // ------------------------------------------------------------------ argument shapes

    private static string SingleString(BsonArray args, string call) =>
        args.Count == 1 && args[0].IsString && !string.IsNullOrWhiteSpace(args[0].AsString)
            ? args[0].AsString
            : throw Error($"{call} takes one name in quotes.");

    private static BsonDocument? OptionalDocument(BsonArray args, int index, string what)
    {
        if (args.Count <= index || args[index].IsBsonNull) return null;
        return args[index] as BsonDocument ?? throw Error($"The {what} must be a {{ … }} document.");
    }

    private static long Number(BsonArray args, string call) =>
        args.Count == 1 && args[0].IsNumeric
            ? Convert.ToInt64(args[0].ToDouble(), CultureInfo.InvariantCulture)
            : throw Error($"{call} takes one number.");

    private static string Excerpt(string s, int pos)
    {
        var rest = pos < s.Length ? s[pos..] : string.Empty;
        return rest.Length > 40 ? rest[..40] + "…" : rest;
    }

    private static FormatException Error(string message) => new(message);
}

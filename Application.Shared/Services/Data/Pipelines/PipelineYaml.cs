using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Renders a pipeline as YAML and parses it back. The stored document is always JSON
/// (<c>Pipeline.GraphJson</c>); this is a <em>view</em> of it, for people who would rather type a pipeline
/// than draw one, and for moving a pipeline between environments.
/// <para>
/// Three shaping decisions make the YAML worth reading, and all three are what make the round-trip
/// non-trivial:
/// </para>
/// <list type="number">
/// <item>
/// <b>There is no <c>edges:</c> array.</b> Each step carries <c>from:</c> naming its upstream steps, and
/// omitting it on a non-source step means "the step listed just above". A straight-line pipeline therefore
/// needs no wiring at all, while a real DAG still round-trips exactly.
/// </item>
/// <item>
/// <b>Config is flattened onto the step.</b> <c>mode: upsert</c>, not <c>config: {mode: upsert}</c>. The
/// catalogue already knows which keys belong to which type, so the split between structure and config is
/// derived rather than a second list to maintain.
/// </item>
/// <item>
/// <b>Layout, viewport and cached schemas are omitted.</b> They are derived or cosmetic. Emitting them
/// would mean a hand-edited file was mostly coordinates, and dragging one node would rewrite the document.
/// Parsing preserves whatever the caller already had — see <see cref="FromYaml"/>.
/// </item>
/// </list>
/// <para>
/// Edge ids are regenerated deterministically from their endpoints, because YAML does not carry them. So
/// JSON -> YAML -> JSON can differ in edge ids alone, while YAML -> JSON -> YAML is stable, which is the
/// property that actually matters: the editor must never rewrite the user's file just by opening it.
/// </para>
/// </summary>
public static class PipelineYaml
{
    /// <summary>Keys on a step that describe structure rather than configuration.</summary>
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "type", "label", "from", "onError", "retry", "timeoutSeconds", "freshness", "parallelGroup"
    };

    private static ISerializer Serializer => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static IDeserializer Deserializer => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    // ---------------------------------------------------------------- to YAML

    public static string ToYaml(PipelineGraph graph)
    {
        var doc = new Dictionary<string, object?>
        {
            ["version"] = graph.SchemaVersion
        };

        var settings = SettingsToMap(graph.Settings);
        if (settings.Count > 0) doc["settings"] = settings;

        // Upstream lookups, and the previous-step rule that lets a linear pipeline omit `from:`.
        var inbound = graph.Edges
            .GroupBy(e => e.To, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var steps = new List<object?>();
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            var spec = PipelineNodeCatalog.Get(node.Type);
            var step = new Dictionary<string, object?>
            {
                ["id"] = node.Id,
                ["type"] = node.Type
            };
            if (!string.IsNullOrWhiteSpace(node.Label)) step["label"] = node.Label;

            // `from` is emitted in port order (left before right for a join) so parsing can map it back
            // positionally, and skipped entirely when it is exactly "the previous step".
            var upstream = OrderedUpstream(node, spec, inbound.GetValueOrDefault(node.Id));
            var previousId = i > 0 ? graph.Nodes[i - 1].Id : null;
            var isImplicit = upstream.Count == 1 && upstream[0] == previousId;

            if (upstream.Count > 0 && !isImplicit) step["from"] = upstream;

            foreach (var (key, value) in ConfigToMap(node, spec)) step[key] = value;

            if (!string.IsNullOrWhiteSpace(node.ParallelGroup)) step["parallelGroup"] = node.ParallelGroup;
            if (!string.IsNullOrWhiteSpace(node.OnError)) step["onError"] = node.OnError;
            if (node.TimeoutSeconds is > 0) step["timeoutSeconds"] = node.TimeoutSeconds;
            if (node.Retry is { MaxAttempts: > 1 })
            {
                step["retry"] = new Dictionary<string, object?>
                {
                    ["maxAttempts"] = node.Retry.MaxAttempts,
                    ["backoffMs"] = node.Retry.BackoffMs
                };
            }

            if (FreshnessToMap(node.Freshness) is { } freshness) step["freshness"] = freshness;

            steps.Add(step);
        }

        doc["nodes"] = steps;
        return Serializer.Serialize(doc);
    }

    private static Dictionary<string, object?> SettingsToMap(PipelineSettings settings)
    {
        // Only non-default values, so the common file stays short and every line present means something.
        var defaults = new PipelineSettings();
        var map = new Dictionary<string, object?>();

        if (settings.OnError != defaults.OnError) map["onError"] = settings.OnError;
        if (settings.TimeoutSeconds != defaults.TimeoutSeconds) map["timeoutSeconds"] = settings.TimeoutSeconds;
        if (settings.FailOnEmptySource != defaults.FailOnEmptySource)
            map["failOnEmptySource"] = settings.FailOnEmptySource;
        if (settings.MaxParallelSteps != defaults.MaxParallelSteps)
            map["maxParallelSteps"] = settings.MaxParallelSteps;

        if (FreshnessToMap(settings.Freshness) is { } freshness) map["freshness"] = freshness;

        return map;
    }

    /// <summary>
    /// A freshness policy as a YAML map, or null when there is nothing to write.
    /// <para>
    /// <c>enabled</c> is emitted only when false, because that is the only case it carries information: an
    /// explicit opt-out. Writing <c>enabled: true</c> on every policy would double the size of the block
    /// and say nothing.
    /// </para>
    /// </summary>
    private static Dictionary<string, object?>? FreshnessToMap(PipelineFreshnessPolicy? policy)
    {
        if (policy is null) return null;

        var map = new Dictionary<string, object?>();
        var defaults = new PipelineFreshnessPolicy();

        if (!policy.Enabled) map["enabled"] = false;
        if (policy.MaxLagMinutes is { } lag) map["maxLagMinutes"] = lag;
        if (!string.IsNullOrWhiteSpace(policy.Cron)) map["cron"] = policy.Cron;
        if (policy.GraceMinutes != defaults.GraceMinutes) map["graceMinutes"] = policy.GraceMinutes;
        if (!string.IsNullOrWhiteSpace(policy.TimeZone)) map["timeZone"] = policy.TimeZone;

        // An all-defaults policy round-trips to nothing rather than to an empty map, which YamlDotNet would
        // emit as `freshness: {}` — a line that reads like a setting and is not one.
        return map.Count > 0 ? map : null;
    }

    /// <summary>The inverse. Returns null when the key is absent or is not a map.</summary>
    private static PipelineFreshnessPolicy? ReadFreshness(object? value)
    {
        if (value is not Dictionary<object, object?> map) return null;

        var policy = new PipelineFreshnessPolicy();

        if (Get(map, "enabled") is { } enabled && TryBool(enabled, out var isEnabled))
            policy.Enabled = isEnabled;
        if (Get(map, "maxLagMinutes") is { } lag && TryInt(lag, out var maxLag))
            policy.MaxLagMinutes = maxLag;
        if (Text(Get(map, "cron")) is { Length: > 0 } cron)
            policy.Cron = cron;
        if (Get(map, "graceMinutes") is { } grace && TryInt(grace, out var graceMinutes))
            policy.GraceMinutes = graceMinutes;
        if (Text(Get(map, "timeZone")) is { Length: > 0 } zone)
            policy.TimeZone = zone;

        return policy;
    }

    /// <summary>
    /// Upstream node ids ordered so position carries the port. For a join that means left then right;
    /// for a multi-input step it is edge order.
    /// </summary>
    private static List<string> OrderedUpstream(
        PipelineNodeDef node, PipelineNodeSpec? spec, List<PipelineEdgeDef>? edges)
    {
        if (edges is null || edges.Count == 0) return [];
        if (spec is null) return edges.Select(e => e.From).ToList();

        var ordered = new List<string>();
        foreach (var port in spec.InPorts)
            ordered.AddRange(edges.Where(e => PortOf(e) == port).Select(e => e.From));

        // Anything on an unrecognized port still has to survive a round-trip.
        ordered.AddRange(edges.Where(e => !spec.InPorts.Contains(PortOf(e))).Select(e => e.From));
        return ordered;
    }

    private static string PortOf(PipelineEdgeDef edge) =>
        string.IsNullOrWhiteSpace(edge.ToPort) ? PipelinePorts.In : edge.ToPort;

    /// <summary>Config, in catalogue field order so every step of a given type reads the same way.</summary>
    private static IEnumerable<KeyValuePair<string, object?>> ConfigToMap(
        PipelineNodeDef node, PipelineNodeSpec? spec)
    {
        if (node.Config is null) yield break;

        var ordered = spec is null
            ? node.Config.Select(kv => kv.Key)
            : spec.Fields.Select(f => f.Key).Where(node.Config.ContainsKey)
                .Concat(node.Config.Select(kv => kv.Key).Where(k => spec.Fields.All(f => f.Key != k)));

        foreach (var key in ordered.Distinct(StringComparer.Ordinal))
        {
            if (ReservedKeys.Contains(key)) continue;   // a config field must not shadow structure
            var value = FromJson(node.Config[key]);
            if (value is not null) yield return new(key, value);
        }
    }

    /// <summary>JsonNode -> plain CLR objects YamlDotNet knows how to emit.</summary>
    private static object? FromJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(kv => kv.Key, kv => FromJson(kv.Value)),
        JsonArray arr => arr.Select(FromJson).ToList(),
        JsonValue value => Scalar(value),
        _ => node.ToString()
    };

    private static object? Scalar(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<string>(out var s)) return s;
        return value.ToString();
    }

    // -------------------------------------------------------------- from YAML

    /// <summary>
    /// Parses YAML back into a graph.
    /// <para>
    /// <paramref name="existing"/> is the graph currently open in the editor, and it exists purely to carry
    /// forward what YAML deliberately does not describe: pinned positions, the viewport, and the cached
    /// column schemas. Without it, applying a one-line YAML edit would silently unpin every node and throw
    /// away the schema cache the mapping grid depends on.
    /// </para>
    /// </summary>
    public static PipelineYamlParseResult FromYaml(string? yaml, PipelineGraph? existing = null)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return PipelineYamlParseResult.Failed("The YAML is empty.");

        Dictionary<object, object?>? root;
        try
        {
            root = Deserializer.Deserialize<Dictionary<object, object?>>(yaml);
        }
        catch (YamlException ex)
        {
            // Mark is 1-based and is the single most useful thing to hand back to a text editor.
            var where = ex.Start.Line > 0 ? $"Line {ex.Start.Line}: " : string.Empty;
            return PipelineYamlParseResult.Failed($"{where}{ex.Message}");
        }

        if (root is null) return PipelineYamlParseResult.Failed("The YAML did not contain a pipeline.");

        var graph = new PipelineGraph();

        if (Get(root, "version") is { } version && TryInt(version, out var schemaVersion))
            graph.SchemaVersion = schemaVersion;

        if (Get(root, "settings") is Dictionary<object, object?> settings)
            ApplySettings(graph.Settings, settings);

        if (Get(root, "nodes") is not List<object?> rawNodes || rawNodes.Count == 0)
            return PipelineYamlParseResult.Failed("Add at least one step under 'nodes:'.");

        // Pass 1: the steps themselves, so pass 2 can resolve `from` against known ids.
        var upstreamByNode = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var implicitChain = new List<string>();

        for (var i = 0; i < rawNodes.Count; i++)
        {
            if (rawNodes[i] is not Dictionary<object, object?> step)
                return PipelineYamlParseResult.Failed($"Step {i + 1} is not a mapping.");

            var id = Text(Get(step, "id"));
            if (string.IsNullOrWhiteSpace(id))
                return PipelineYamlParseResult.Failed($"Step {i + 1} has no 'id'.");

            var type = Text(Get(step, "type"));
            if (string.IsNullOrWhiteSpace(type))
                return PipelineYamlParseResult.Failed($"Step '{id}' has no 'type'.");

            if (!PipelineNodeCatalog.IsKnown(type))
            {
                return PipelineYamlParseResult.Failed(
                    $"Step '{id}' has type '{type}', which is not a known step type. " +
                    $"Valid types: {string.Join(", ", PipelineNodeCatalog.All.Select(s => s.Type))}.");
            }

            var spec = PipelineNodeCatalog.Get(type)!;
            var node = new PipelineNodeDef { Id = id!, Type = type! };

            if (Text(Get(step, "label")) is { Length: > 0 } label) node.Label = label;
            if (Text(Get(step, "parallelGroup")) is { Length: > 0 } group) node.ParallelGroup = group;
            if (Text(Get(step, "onError")) is { Length: > 0 } onError) node.OnError = onError;
            if (Get(step, "timeoutSeconds") is { } timeout && TryInt(timeout, out var seconds))
                node.TimeoutSeconds = seconds;

            if (Get(step, "retry") is Dictionary<object, object?> retry)
            {
                node.Retry = new PipelineRetryDef();
                if (Get(retry, "maxAttempts") is { } attempts && TryInt(attempts, out var maxAttempts))
                    node.Retry.MaxAttempts = maxAttempts;
                if (Get(retry, "backoffMs") is { } backoff && TryInt(backoff, out var backoffMs))
                    node.Retry.BackoffMs = backoffMs;
            }

            node.Freshness = ReadFreshness(Get(step, "freshness"));

            var config = new JsonObject();
            foreach (var (rawKey, rawValue) in step)
            {
                var key = Text(rawKey);
                if (string.IsNullOrEmpty(key) || ReservedKeys.Contains(key)) continue;
                config[key] = ToJson(rawValue);
            }
            if (config.Count > 0) node.Config = config;

            graph.Nodes.Add(node);

            // `from` resolution, including the implicit "previous step" rule. Sources never take an
            // implicit input — that is what makes a file source followed by a filter unambiguous.
            var from = StringList(Get(step, "from"));
            if (from.Count == 0 && !spec.IsSource && implicitChain.Count > 0)
                from = [implicitChain[^1]];

            upstreamByNode[id!] = from;
            implicitChain.Add(id!);
        }

        // Pass 2: edges, with position mapped onto ports.
        foreach (var node in graph.Nodes)
        {
            var spec = PipelineNodeCatalog.Get(node.Type)!;
            var upstream = upstreamByNode.GetValueOrDefault(node.Id) ?? [];

            for (var i = 0; i < upstream.Count; i++)
            {
                var fromId = upstream[i];
                if (graph.Nodes.All(n => n.Id != fromId))
                {
                    return PipelineYamlParseResult.Failed(
                        $"Step '{node.Id}' reads from '{fromId}', which is not a step in this pipeline.");
                }

                // One port per position while ports last, then everything piles onto the final port —
                // which is exactly right for a multi-input step and caught by the compiler otherwise.
                var port = spec.InPorts.Count == 0
                    ? PipelinePorts.In
                    : spec.InPorts[Math.Min(i, spec.InPorts.Count - 1)];

                graph.Edges.Add(new PipelineEdgeDef
                {
                    Id = EdgeId(fromId, node.Id, port),
                    From = fromId,
                    FromPort = PipelinePorts.Out,
                    To = node.Id,
                    ToPort = port
                });
            }
        }

        // Carry over everything YAML deliberately does not describe.
        if (existing is not null)
        {
            graph.Layout = existing.Layout;
            graph.Viewport = existing.Viewport;

            // Only for nodes that still exist, or a rename would leave a stale schema behind forever.
            if (existing.Schemas is not null)
            {
                var live = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
                var kept = existing.Schemas
                    .Where(kv => live.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                if (kept.Count > 0) graph.Schemas = kept;
            }
        }

        return new PipelineYamlParseResult(graph, null);
    }

    /// <summary>
    /// Deterministic, so YAML -> JSON -> YAML does not churn. A port is part of the id because a join can
    /// legitimately take both inputs from the same upstream step.
    /// </summary>
    private static string EdgeId(string from, string to, string port) => $"{from}--{port}--{to}";

    private static void ApplySettings(PipelineSettings target, Dictionary<object, object?> map)
    {
        if (Text(Get(map, "onError")) is { Length: > 0 } onError) target.OnError = onError;
        if (Get(map, "timeoutSeconds") is { } timeout && TryInt(timeout, out var seconds))
            target.TimeoutSeconds = seconds;
        if (Get(map, "failOnEmptySource") is { } fail && TryBool(fail, out var failOnEmpty))
            target.FailOnEmptySource = failOnEmpty;
        if (Get(map, "maxParallelSteps") is { } parallel && TryInt(parallel, out var maxParallel))
            target.MaxParallelSteps = maxParallel;
        target.Freshness = ReadFreshness(Get(map, "freshness"));
    }

    // ---------------------------------------------------------------- helpers

    private static object? Get(Dictionary<object, object?> map, string key)
    {
        foreach (var (k, v) in map)
            if (string.Equals(Text(k), key, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    private static string? Text(object? value) => value?.ToString();

    private static bool TryInt(object? value, out int result) =>
        int.TryParse(value?.ToString(), out result);

    private static bool TryBool(object? value, out bool result) =>
        bool.TryParse(value?.ToString(), out result);

    private static List<string> StringList(object? value) => value switch
    {
        null => [],
        List<object?> list => list.Select(Text).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList(),
        // A bare scalar is the natural way to write a single upstream step, so accept it.
        _ => Text(value) is { Length: > 0 } single ? [single] : []
    };

    /// <summary>
    /// Plain YAML objects -> JsonNode. Scalars arrive as strings, so numbers and booleans are recovered
    /// here; without that, <c>batchSize: 5000</c> would be stored as the string "5000" and the executor's
    /// typed accessors would quietly see nothing.
    /// </summary>
    private static JsonNode? ToJson(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case Dictionary<object, object?> map:
            {
                var obj = new JsonObject();
                foreach (var (k, v) in map)
                {
                    var key = Text(k);
                    if (!string.IsNullOrEmpty(key)) obj[key] = ToJson(v);
                }
                return obj;
            }

            case List<object?> list:
            {
                var arr = new JsonArray();
                foreach (var item in list) arr.Add(ToJson(item));
                return arr;
            }

            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return JsonValue.Create(d);
        }

        var text = value.ToString() ?? string.Empty;

        if (bool.TryParse(text, out var parsedBool)) return JsonValue.Create(parsedBool);
        if (int.TryParse(text, out var parsedInt) && parsedInt.ToString() == text)
            return JsonValue.Create(parsedInt);
        if (long.TryParse(text, out var parsedLong) && parsedLong.ToString() == text)
            return JsonValue.Create(parsedLong);

        // Everything else stays a string. Notably a value like "0123" must NOT become 123 — leading zeros
        // are meaningful in exactly the item and store codes this feature exists to move around, which is
        // why the round-trip check above is there rather than a bare TryParse.
        return JsonValue.Create(text);
    }
}

/// <summary>Either a parsed graph or a single human-readable reason it could not be parsed.</summary>
public sealed record PipelineYamlParseResult(PipelineGraph? Graph, string? Error)
{
    public bool Success => Graph is not null;

    public static PipelineYamlParseResult Failed(string error) => new(null, error);
}

using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The conditional-split step: routes each row to one of several outputs.
/// <para>
/// This is the only node type whose <b>ports depend on its configuration</b> and the only one that produces
/// more than one relation, so it gets its own file rather than another case in <see cref="PipelineSql"/>.
/// Two properties are worth stating because everything here exists to guarantee them:
/// </para>
/// <list type="number">
/// <item>
/// <b>Every row lands in exactly one output.</b> Conditions are evaluated in order and each output excludes
/// the ones above it, so overlapping conditions do not duplicate a row — the first match wins, the way a
/// chain of if/else-if reads.
/// </item>
/// <item>
/// <b>A condition that evaluates to NULL is not a match.</b> In SQL <c>NOT NULL</c> is NULL, so a naive
/// exclusion chain would drop such a row from every output including the default — silently losing rows,
/// which is the one thing a router must never do. Each condition is therefore wrapped in
/// <c>COALESCE(…, false)</c>.
/// </item>
/// </list>
/// </summary>
public static class PipelineSwitch
{
    /// <summary>Port name for rows matching no condition. Reserved — an output may not be called this.</summary>
    public const string DefaultPort = "rest";

    /// <summary>
    /// The ports a switch node exposes, derived from its config. Always ends with <see cref="DefaultPort"/>:
    /// without somewhere for unmatched rows to go they would be dropped, and a router that quietly discards
    /// is worse than one that makes you wire up an output you may not use.
    /// </summary>
    public static IReadOnlyList<string> PortsFor(PipelineNodeDef node)
    {
        var ports = new List<string>();

        foreach (var row in Outputs(node.Config))
        {
            var port = Sanitize(Str(row, "port"));
            if (port is not null && port != DefaultPort && !ports.Contains(port, StringComparer.Ordinal))
                ports.Add(port);
        }

        // A brand-new node has no outputs configured yet, and a node with no ports at all cannot be
        // connected — so it would be impossible to get started. One placeholder keeps it wireable.
        if (ports.Count == 0) ports.Add("match");

        ports.Add(DefaultPort);
        return ports;
    }

    /// <summary>
    /// One SELECT per output port. Errors come back rather than being thrown, matching every other builder.
    /// </summary>
    public static SwitchBuildResult Build(
        PipelineNodeDef node, IReadOnlyDictionary<string, RelationInput> inputs)
    {
        if (!inputs.TryGetValue(PipelinePorts.In, out var input))
            return SwitchBuildResult.Fail("This step has no input.", PipelineErrorType.Invalid);

        var rows = Outputs(node.Config).ToList();
        if (rows.Count == 0)
            return SwitchBuildResult.Fail(
                "This step has no conditions, so it would route everything to the leftover output.",
                PipelineErrorType.Invalid);

        var conditions = new List<(string Port, string Condition)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var port = Sanitize(Str(row, "port"));
            var condition = Str(row, "condition");

            if (port is null)
                return SwitchBuildResult.Fail(
                    "An output has no name. Names may contain letters, numbers and underscores.",
                    PipelineErrorType.Invalid);

            if (port == DefaultPort)
                return SwitchBuildResult.Fail(
                    $"'{DefaultPort}' is the name of the built-in leftover output, so an output cannot use it.",
                    PipelineErrorType.Invalid);

            if (!seen.Add(port))
                return SwitchBuildResult.Fail(
                    $"Two outputs are both called '{port}'.", PipelineErrorType.Invalid);

            if (string.IsNullOrWhiteSpace(condition))
                return SwitchBuildResult.Fail(
                    $"Output '{port}' has no condition.", PipelineErrorType.Invalid);

            conditions.Add((port, condition!));
        }

        var statements = new List<SwitchOutput>();
        var exclusions = new List<string>();

        foreach (var (port, condition) in conditions)
        {
            var matched = Truthy(condition);

            // Everything above this output is excluded, so first match wins and no row is duplicated.
            var clause = exclusions.Count == 0
                ? matched
                : $"{matched} AND {string.Join(" AND ", exclusions.Select(e => $"NOT {e}"))}";

            statements.Add(new SwitchOutput(port,
                $"SELECT * FROM {PipelineSql.Q(input.Relation)} WHERE {clause}"));

            exclusions.Add(matched);
        }

        var leftover = string.Join(" AND ", exclusions.Select(e => $"NOT {e}"));
        statements.Add(new SwitchOutput(DefaultPort,
            $"SELECT * FROM {PipelineSql.Q(input.Relation)} WHERE {leftover}"));

        return SwitchBuildResult.Ok(statements);
    }

    /// <summary>
    /// Wraps a condition so NULL counts as "did not match" rather than poisoning the exclusion chain.
    /// </summary>
    private static string Truthy(string condition) => $"COALESCE(({condition}), false)";

    /// <summary>
    /// Port names become part of a relation name and are interpolated into SQL, so anything outside
    /// letters, digits and underscore is refused rather than escaped — a port is an identifier the author
    /// chooses once, not free text.
    /// </summary>
    private static string? Sanitize(string? port)
    {
        if (string.IsNullOrWhiteSpace(port)) return null;

        var trimmed = port!.Trim();
        return trimmed.All(c => char.IsLetterOrDigit(c) || c == '_') ? trimmed : null;
    }

    private static IEnumerable<JsonObject> Outputs(JsonObject? config) =>
        config?["outputs"] switch
        {
            JsonArray array => array.OfType<JsonObject>(),
            JsonObject single => [single],
            _ => []
        };

    private static string? Str(JsonObject? row, string key)
    {
        var value = row?[key];
        return value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;
    }
}

public sealed record SwitchOutput(string Port, string Sql);

public sealed record SwitchBuildResult(
    bool Success, IReadOnlyList<SwitchOutput> Outputs, string? Error, string? ErrorType)
{
    public static SwitchBuildResult Ok(IReadOnlyList<SwitchOutput> outputs) =>
        new(true, outputs, null, null);

    public static SwitchBuildResult Fail(string error, string errorType) =>
        new(false, [], error, errorType);
}

/// <summary>
/// How a node's output port maps to the relation holding its rows.
/// <para>
/// The default port keeps the plain node id, so every existing step's relation name is byte-identical to
/// what it was before switches existed. Only a named port gets a suffix — which means a pipeline with no
/// switch in it produces exactly the same SQL as before.
/// </para>
/// </summary>
public static class PipelineRelations
{
    public static string For(string nodeId, string? fromPort) =>
        string.IsNullOrEmpty(fromPort) || fromPort == PipelinePorts.Out
            ? nodeId
            : $"{nodeId}__{fromPort}";
}

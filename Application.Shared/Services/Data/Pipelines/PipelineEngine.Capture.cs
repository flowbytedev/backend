using System.Globalization;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The <c>transform.capture</c> step: reads named scalars out of its input and publishes them as
/// <c>{{ vars.* }}</c>.
/// <para>
/// This is the one step that exists for a side effect. Everything else in the engine turns a relation into
/// another relation; this one also writes into <c>ctx.Vars</c>, which later steps read through config
/// substitution. It passes its input through unchanged so the graph shape stays ordinary — a capture can sit
/// mid-chain without diverting the data.
/// </para>
/// </summary>
public partial class PipelineEngine
{
    private async Task<NodeOutcome> ExecuteCaptureAsync(
        ExecutionContext ctx, PipelineNodeDef node, string nodeId, CancellationToken ct)
    {
        var config = node.Config;

        var upstream = ctx.Plan.InputOn(node.Id, PipelinePorts.In);
        if (upstream is null)
            return NodeOutcome.Failed("This step has no input.", PipelineErrorType.Invalid);

        var values = Expressions(config);
        if (values.Count == 0)
            return NodeOutcome.Failed(
                "This step captures nothing — add a name and an expression.", PipelineErrorType.Invalid);

        var orderBy = Str(config, "orderBy");

        // One row, one column per captured value. LIMIT 1 with no ORDER BY is only sound when every
        // expression is an aggregate; the compiler enforces that, and this is where it would otherwise bite.
        var projection = string.Join(", ",
            values.Select(v => $"({v.Expression}) AS {QuoteIdentifier(v.Name)}"));

        var sql = $"SELECT {projection} FROM {QuoteRelation(upstream)}"
                  + (string.IsNullOrWhiteSpace(orderBy) ? string.Empty : $" ORDER BY {orderBy}")
                  + " LIMIT 1";

        var read = await store.ReadRowAsync(ctx.ScratchDatasetId, sql, ct: ct);

        if (!read.Success)
            return NodeOutcome.Failed(read.Error ?? "The values could not be read.",
                PipelineErrorType.SqlError, sql);

        if (read.Row is null)
        {
            // No rows. Failing is the default because the alternative is a variable that quietly becomes
            // empty, and an empty value in a WHERE clause or a URL does not announce itself.
            if ((Str(config, "onEmpty") ?? PipelineCaptureEmptyBehaviour.Fail)
                == PipelineCaptureEmptyBehaviour.Fail)
            {
                return NodeOutcome.Failed(
                    "This step captures values but its input has no rows, so there is nothing to read.",
                    PipelineErrorType.EmptySource, sql);
            }

            foreach (var value in values)
            {
                ctx.Vars[$"{PipelineTokens.VarsRoot}.{value.Name}"] = string.Empty;
                ctx.Log.WriteLine($"      {value.Name} = (empty — no rows)");
            }
        }
        else
        {
            foreach (var value in values)
            {
                var raw = read.Row.GetValueOrDefault(value.Name);

                // Portable() is the watermark's own conversion, reused so a captured date and an
                // incremental high-water mark of the same column produce the same text. Two formatters for
                // one job is how one of them ends up emitting MM/dd/yyyy.
                var text = PipelineWatermarkWindow.Portable(raw) ?? string.Empty;

                ctx.Vars[$"{PipelineTokens.VarsRoot}.{value.Name}"] = text;

                ctx.Log.WriteLine($"      {value.Name} = {Describe(text, raw)}");
            }
        }

        // Passed through, not consumed: a capture sits in the middle of a chain and the rows carry on. The
        // relation is a view over the input rather than a copy, so this costs nothing.
        var materialized = await store.MaterializeAsync(
            ctx.ScratchDatasetId, nodeId, $"SELECT * FROM {QuoteRelation(upstream)}",
            node.TimeoutSeconds, ct);

        return NodeOutcome.From(materialized);
    }

    /// <summary>
    /// A captured value as it appears in the log. A null is named rather than shown as blank, because
    /// "the value is empty" and "there was no value" lead to different fixes.
    /// </summary>
    private static string Describe(string text, object? raw) =>
        raw is null or DBNull
            ? "(null)"
            : text.Length == 0 ? "(empty)" : text;

    /// <summary>
    /// The (name, expression) rows, in order. Shares the shape of <c>transform.compute</c>'s expression list
    /// so the same inspector editor serves both.
    /// </summary>
    internal static List<CapturedValue> Expressions(JsonObject? config)
    {
        var result = new List<CapturedValue>();

        if (config?["values"] is not JsonObject obj) return result;

        foreach (var (name, expression) in obj)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var text = expression is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrWhiteSpace(text)) continue;

            result.Add(new CapturedValue(name, text!));
        }

        return result;
    }

    internal sealed record CapturedValue(string Name, string Expression);

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"") + '"';
}

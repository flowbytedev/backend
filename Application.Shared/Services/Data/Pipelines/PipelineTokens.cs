using System.Text;
using System.Text.Json.Nodes;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// The <c>{{ }}</c> substitution used in pipeline config — run metadata and run parameters, nothing else.
/// <para>
/// This is deliberately far smaller than the equivalent in a general workflow engine, and the reason is
/// structural: a workflow passes <em>values</em> between nodes, so it needs a way to name another node's
/// output. A pipeline passes <em>relations</em>, so data never travels through config at all. That leaves
/// only two genuine needs — "what time is it" and "what did the caller ask for" — which is why there are
/// exactly two roots and no path into node outputs.
/// </para>
/// <para>
/// No filters, no arithmetic, no function calls. Every one of those has a structured home in the node
/// catalogue (a compute node, an aggregate row), and a mini-language the visual editor cannot render as a
/// form is a mini-language the visual editor cannot author.
/// </para>
/// </summary>
public static class PipelineTokens
{
    /// <summary>Run metadata: <c>run.id</c>, <c>run.date</c>, <c>run.startedAt</c>, …</summary>
    public const string RunRoot = "run";

    /// <summary>Values supplied when the run was started (manual form or API body).</summary>
    public const string ParamsRoot = "params";

    /// <summary>
    /// Values captured from a node's output by a <c>transform.capture</c> step.
    /// <para>
    /// This is the root the class comment above used to say could not exist, and the reasoning there still
    /// holds for <em>rows</em>: data does not travel through config, and a relation never will. What travels
    /// here is a single scalar, deliberately extracted, for the cases a relation genuinely cannot serve — a
    /// URL, a file name, an email subject, a table name. Anything that ends up in the data should still be a
    /// join, not a variable.
    /// </para>
    /// <para>
    /// Unlike <see cref="RunRoot"/>, a value here is <b>external data</b>. It must be escaped for wherever it
    /// is being substituted — see <c>PipelineTokenContexts</c>.
    /// </para>
    /// </summary>
    public const string VarsRoot = "vars";

    public static readonly string[] Roots = [RunRoot, ParamsRoot, VarsRoot];

    /// <summary>The <c>run.*</c> keys this build provides, for the editor's token helper.</summary>
    public static readonly string[] RunKeys =
    [
        "run.id", "run.pipelineId", "run.pipelineName", "run.date", "run.startedAt", "run.year",
        "run.month", "run.day"
    ];

    /// <summary>
    /// Every distinct <c>{{ path }}</c> appearing anywhere in a config tree. Used by the compiler to catch
    /// typos at save time, and by the editor to show which tokens a node actually uses.
    /// </summary>
    public static IReadOnlyList<string> ReferencedPaths(JsonNode? config)
    {
        var found = new List<string>();
        if (config is null) return found;

        Walk(config, found);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(JsonNode? node, List<string> found)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj) Walk(kv.Value, found);
                break;
            case JsonArray arr:
                foreach (var item in arr) Walk(item, found);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                foreach (var path in PathsIn(text)) found.Add(path);
                break;
        }
    }

    /// <summary>Extracts the paths from the <c>{{ }}</c> placeholders in one string.</summary>
    public static IEnumerable<string> PathsIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var from = 0;
        while (true)
        {
            var open = text.IndexOf("{{", from, StringComparison.Ordinal);
            if (open < 0) yield break;

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) yield break;

            var path = text[(open + 2)..close].Trim();
            if (path.Length > 0) yield return path;

            from = close + 2;
        }
    }

    /// <summary>
    /// Substitutes every <c>{{ path }}</c> using <paramref name="lookup"/>.
    /// <para>
    /// An unresolved token throws rather than being left in place. Leaving it would be far worse than
    /// failing: the literal text <c>{{ run.date }}</c> would sail into a file path or a WHERE clause, and
    /// the run would either read the wrong data or quietly find no rows and report success.
    /// </para>
    /// </summary>
    public static string Resolve(string? text, Func<string, string?> lookup)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!text.Contains("{{", StringComparison.Ordinal)) return text;

        var sb = new StringBuilder(text.Length);
        var from = 0;

        while (true)
        {
            var open = text.IndexOf("{{", from, StringComparison.Ordinal);
            if (open < 0) break;

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) break;

            sb.Append(text, from, open - from);

            var path = text[(open + 2)..close].Trim();
            var value = lookup(path)
                ?? throw new UnresolvedTokenException(path);
            sb.Append(value);

            from = close + 2;
        }

        sb.Append(text, from, text.Length - from);
        return sb.ToString();
    }

    /// <summary>Builds the standard <c>run.*</c> values for a run.</summary>
    public static Dictionary<string, string> RunValues(
        string runId, string pipelineId, string? pipelineName, DateTime startedAtUtc) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["run.id"] = runId,
            ["run.pipelineId"] = pipelineId,
            ["run.pipelineName"] = pipelineName ?? string.Empty,
            ["run.date"] = startedAtUtc.ToString("yyyy-MM-dd"),
            ["run.startedAt"] = startedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            ["run.year"] = startedAtUtc.ToString("yyyy"),
            ["run.month"] = startedAtUtc.ToString("MM"),
            ["run.day"] = startedAtUtc.ToString("dd")
        };
}

/// <summary>
/// Thrown when a <c>{{ }}</c> token has no value. Carries the path so the engine can name it in the step
/// error rather than reporting a generic failure.
/// </summary>
public sealed class UnresolvedTokenException(string path)
    : Exception($"'{{{{ {path} }}}}' could not be resolved.")
{
    public string Path { get; } = path;
}

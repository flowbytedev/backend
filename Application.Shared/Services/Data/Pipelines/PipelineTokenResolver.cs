using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Substitutes <c>{{ }}</c> through a whole node's config, escaping each value for the place it lands.
/// <para>
/// Two problems are solved here that <see cref="PipelineTokens.Resolve"/> alone cannot.
/// </para>
/// <para>
/// <b>Transforms never had substitution at all.</b> The engine handed <c>PipelineSql.Build</c> the raw node,
/// so a <c>{{ run.date }}</c> in a filter reached DuckDB as those literal characters — while the compiler
/// happily validated the token, telling the author it was supported. Resolving the config into a copy fixes
/// that for every transform at once and keeps <c>PipelineSql</c> entirely token-unaware.
/// </para>
/// <para>
/// <b>Not every value is ours.</b> <c>run.*</c> is generated here and safe anywhere. <c>params.*</c> comes
/// from an API trigger's body and <c>vars.*</c> from whatever a source returned — both external. Substituting
/// those verbatim into SQL or a URL is an injection through the pipeline's own data. So each field declares
/// its <see cref="PipelineTokenContexts"/> and the value is escaped for it.
/// </para>
/// </summary>
public static class PipelineTokenResolver
{
    /// <summary>
    /// A copy of <paramref name="node"/> whose config has every token substituted and escaped. The original
    /// is left untouched — the graph is a stored document and a run must not rewrite it.
    /// <para>
    /// Returns the node itself when its config holds no tokens at all, which is the overwhelmingly common
    /// case and keeps a deep clone off the hot path.
    /// </para>
    /// </summary>
    public static PipelineNodeDef Resolve(
        PipelineNodeDef node, PipelineNodeSpec? spec, Func<string, string?> lookup)
    {
        if (node.Config is null) return node;
        if (PipelineTokens.ReferencedPaths(node.Config).Count == 0) return node;

        var contexts = Contexts(spec);
        var resolved = new JsonObject();

        foreach (var (key, value) in node.Config)
        {
            var context = contexts.GetValueOrDefault(key, PipelineTokenContexts.Plain);
            resolved[key] = Walk(value, context, lookup);
        }

        // A shallow copy with the new config: everything else about the node (id, type, retry, error mode)
        // is structure rather than content and must not be duplicated divergently.
        return new PipelineNodeDef
        {
            Id = node.Id,
            Type = node.Type,
            Label = node.Label,
            Config = resolved,
            OnError = node.OnError,
            Retry = node.Retry,
            TimeoutSeconds = node.TimeoutSeconds
        };
    }

    /// <summary>
    /// Field key to token context, from the catalogue. A field the catalogue does not know about — a stored
    /// graph can carry one after a rename — falls back to Plain, which never corrupts a value.
    /// </summary>
    private static Dictionary<string, string> Contexts(PipelineNodeSpec? spec)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec is null) return map;

        foreach (var field in spec.Fields)
            map[field.Key] = field.TokenContext;

        return map;
    }

    /// <summary>
    /// Substitutes through a JSON subtree. The context is inherited by everything below a field, because a
    /// structured field's rows hold values of the same kind as the field itself — every row of an
    /// expression list is SQL.
    /// </summary>
    private static JsonNode? Walk(JsonNode? node, string context, Func<string, string?> lookup)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj) copy[key] = Walk(value, context, lookup);
                return copy;
            }

            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array) copy.Add(Walk(item, context, lookup));
                return copy;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Substitute(text, context, lookup));

            default:
                // Numbers, booleans and nulls cannot carry a token. Cloned so the caller owns the tree.
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// One string's worth of substitution, escaping each value for <paramref name="context"/>.
    /// <para>
    /// Escaping happens per value rather than on the finished string, which is the only order that works: by
    /// the time the values are pasted in, there is no way to tell which characters came from the author and
    /// which came from the data.
    /// </para>
    /// </summary>
    internal static string Substitute(string? text, string context, Func<string, string?> lookup) =>
        PipelineTokens.Resolve(text, path =>
        {
            var value = lookup(path);
            return value is null ? null : Escape(value, context, path);
        });

    /// <summary>
    /// Escapes one substituted value for where it is going.
    /// <para>
    /// <c>run.*</c> is exempt: those values are generated by this app in fixed formats, and escaping
    /// <c>run.date</c> as a SQL literal would wrap it in quotes an author has already written around it. The
    /// exemption is by root, not by looking at the value, so it cannot be spoofed by data.
    /// </para>
    /// </summary>
    internal static string Escape(string value, string context, string path)
    {
        if (path.StartsWith(PipelineTokens.RunRoot + ".", StringComparison.OrdinalIgnoreCase))
            return value;

        return context switch
        {
            // Reuses PipelineWatermarkWindow's rule on purpose: that value also goes into a WHERE clause, and two
            // different quoting rules for the same job is how one of them ends up wrong.
            PipelineTokenContexts.Sql => PipelineWatermarkWindow.Literal(value),
            PipelineTokenContexts.Url => Uri.EscapeDataString(value),
            PipelineTokenContexts.Path => SafePathSegment(value),
            PipelineTokenContexts.Json => JsonStringContent(value),
            _ => value
        };
    }

    /// <summary>
    /// A value safe to drop between the quotes of a JSON string the author wrote. Escapes what JSON
    /// requires — quote, backslash, control characters — and nothing else.
    /// <para>
    /// The relaxed encoder rather than the default one: the default also escapes <c>&lt;</c>, <c>&amp;</c>
    /// and <c>+</c> to <c>\uXXXX</c> for safety in HTML, which this is not — the finished string is an HTTP
    /// request body. Either decodes to the same value, but the substituted template is parsed again before
    /// it is sent, and a parse error reads far better when the text still looks like what was typed.
    /// </para>
    /// </summary>
    internal static string JsonStringContent(string value) =>
        JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value;

    /// <summary>
    /// A value safe to drop into a path or file name: no separators, no traversal, no wildcards.
    /// <para>
    /// Wildcards are stripped too, unlike in the export writer's file-name cleaner, because a file source
    /// treats a glob as a pattern — a captured <c>*</c> would silently widen which files a run reads.
    /// </para>
    /// </summary>
    internal static string SafePathSegment(string value)
    {
        var cleaned = value;

        foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(bad, '_');

        cleaned = cleaned
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace('*', '_')
            .Replace('?', '_')
            .Trim()
            .TrimStart('.');

        return cleaned;
    }
}

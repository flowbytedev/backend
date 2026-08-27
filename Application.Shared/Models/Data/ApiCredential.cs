using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Application.Shared.Models.Data;

/// <summary>
/// A named, reusable HTTP endpoint credential, referenced by <c>source.api</c> and <c>destination.api</c>
/// pipeline steps.
/// <para>
/// <b>Why this exists at all rather than the token living on the node.</b> A pipeline's graph is stored as
/// plain JSON in <c>pipeline.graph_json</c>, is served to the browser in full, and is round-tripped through
/// the YAML tab where the author reads and edits it by hand. A bearer token in node config would therefore
/// be stored in the clear, rendered in a textarea, and copied into every duplicate of the pipeline. So the
/// node stores only this row's <see cref="Name"/> and the secret never enters the document — the same
/// arrangement <c>source.database</c> has with <see cref="DatabaseConnection"/>.
/// </para>
/// <para>
/// Unlike <see cref="DatabaseWriteCredential"/> and <see cref="DatabaseAdminCredential"/>, this does not
/// hang off a monitored entity. An API is not a registered asset in this system, so there is nothing to
/// attach to; the row is company-scoped and addressed by name.
/// </para>
/// </summary>
public class ApiCredential : BaseModel
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// How a pipeline refers to this credential. Unique per company, because the YAML view addresses it by
    /// name — an ambiguous name there would silently resolve to whichever row came back first.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional. When set, a step's path is resolved against it, so moving from staging to production is one
    /// edit here rather than one per step. A step may still give an absolute URL, but it must be on this
    /// host — see <c>PipelineApiClient.ResolveUrl</c>. That restriction is the point of storing a base:
    /// without it, any step could aim this credential's token at an arbitrary server.
    /// </summary>
    [MaxLength(1000)]
    public string? BaseUrl { get; set; }

    /// <summary>One of <see cref="ApiAuthTypes"/>.</summary>
    [MaxLength(20)]
    public string AuthType { get; set; } = ApiAuthTypes.None;

    /// <summary>Username, for <see cref="ApiAuthTypes.Basic"/>.</summary>
    [MaxLength(200)]
    public string? Username { get; set; }

    /// <summary>
    /// The bearer token, basic password, or API key — encrypted at rest. <see cref="JsonIgnore"/> so it
    /// cannot leave through the API surface even by accident.
    /// </summary>
    [JsonIgnore]
    public string? SecretEncrypted { get; set; }

    /// <summary>Header to carry the secret in, for <see cref="ApiAuthTypes.Header"/>. E.g. <c>X-Api-Key</c>.</summary>
    [MaxLength(100)]
    public string? HeaderName { get; set; }

    /// <summary>
    /// Query-string parameter to carry the secret in, for <see cref="ApiAuthTypes.QueryParam"/>. Supported
    /// because some APIs offer nothing else, but it is the weakest option: query strings are logged by
    /// proxies and servers in a way headers are not.
    /// </summary>
    [MaxLength(100)]
    public string? QueryParamName { get; set; }

    /// <summary>
    /// Static headers sent with every request, as a JSON object. For the unauthenticated constants an API
    /// requires — <c>Accept</c>, a version pin, a tenant id.
    /// </summary>
    public string? ExtraHeadersJson { get; set; }

    /// <summary>
    /// Whether a <c>destination.api</c> step may send data through this credential. Off by default, and
    /// deliberately not inferable: the same reasoning as <see cref="DatabaseWriteCredential.AllowCreateTable"/>.
    /// A read token that turns out to have write scope should not become a write path because someone
    /// dragged a node onto a canvas. A write is refused outright without this, never downgraded.
    /// </summary>
    public bool AllowWrite { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Per-request timeout. Null falls back to <c>Pipelines:ApiTimeoutSeconds</c>.</summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// How the secret is presented to the API. These strings are stored, so they are a contract.
/// <para>
/// No OAuth2 client-credentials flow yet: it needs token caching with expiry shared across processes, which
/// is a different problem from attaching a static secret to a request. A pipeline can still use OAuth by
/// pointing at a gateway that holds the flow.
/// </para>
/// </summary>
public static class ApiAuthTypes
{
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string Basic = "basic";

    /// <summary>Secret in a named header.</summary>
    public const string Header = "header";

    /// <summary>Secret in a named query-string parameter.</summary>
    public const string QueryParam = "query";

    public static readonly string[] All = [None, Bearer, Basic, Header, QueryParam];

    /// <summary>True when this type needs <see cref="ApiCredential.SecretEncrypted"/> populated.</summary>
    public static bool NeedsSecret(string? authType) =>
        authType is Bearer or Basic or Header or QueryParam;
}

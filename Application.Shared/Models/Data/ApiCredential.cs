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
    /// Form field to carry the secret in, for <see cref="ApiAuthTypes.FormField"/>. E.g.
    /// <c>client_secret</c>.
    /// </summary>
    [MaxLength(100)]
    public string? FormFieldName { get; set; }

    /// <summary>
    /// Token endpoint, for <see cref="ApiAuthTypes.OAuth2"/>. An absolute URL, and deliberately exempt from
    /// the <see cref="BaseUrl"/> host restriction — a token endpoint is nearly always on a different host
    /// from the API it issues tokens for.
    /// </summary>
    [MaxLength(1000)]
    public string? TokenUrl { get; set; }

    /// <summary>
    /// Fields posted to the token endpoint, as a JSON object — <c>grant_type</c>, <c>client_id</c>,
    /// <c>scope</c>. Sent form-encoded, each value encoded at send time.
    /// <para>
    /// The secret is NOT in here. It comes from <see cref="SecretEncrypted"/> under the name
    /// <see cref="FormFieldName"/> (defaulting to <c>client_secret</c>), so the one value that needs
    /// encrypting is the one value stored encrypted.
    /// </para>
    /// </summary>
    public string? TokenFieldsJson { get; set; }

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

    /// <summary>
    /// Secret as a named field in a form-encoded request body — what an OAuth2 client-credentials token
    /// request needs for <c>client_secret</c>.
    /// <para>
    /// Without this the only place to put that secret is the step's own body field, which lands in
    /// <c>pipeline.graph_json</c>: stored in the clear, copied onto every run row as its snapshot, and
    /// readable by anyone who can open the YAML tab. This type keeps it encrypted here instead, exactly as
    /// <see cref="Header"/> and <see cref="QueryParam"/> already do for their own positions.
    /// </para>
    /// </summary>
    public const string FormField = "form";

    /// <summary>
    /// OAuth2 client credentials. The client fetches an access token from the credential's token URL, caches
    /// it until it expires, and sends it as a bearer token — so a step using this credential needs no token
    /// request of its own.
    /// <para>
    /// This exists because doing it as pipeline steps is worse in three ways that are easy to miss: the token
    /// lands in <c>pipeline_run_step.output_preview_json</c> in the clear for the retention window,
    /// <c>expires_in</c> is ignored so every run pays for a fresh token, and the token step plus its capture
    /// step have to be repeated in every pipeline that touches the API.
    /// </para>
    /// </summary>
    public const string OAuth2 = "oauth2";

    public static readonly string[] All = [None, Bearer, Basic, Header, QueryParam, FormField, OAuth2];

    /// <summary>True when this type needs <see cref="ApiCredential.SecretEncrypted"/> populated.</summary>
    public static bool NeedsSecret(string? authType) =>
        authType is Bearer or Basic or Header or QueryParam or FormField or OAuth2;

    /// <summary>
    /// True when this type puts the secret in the request body, which constrains what the step may send: the
    /// body has to be form-encoded, and a GET has no body at all.
    /// </summary>
    public static bool IsBodyAuth(string? authType) => authType == FormField;
}

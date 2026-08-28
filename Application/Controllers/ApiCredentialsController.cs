using Application.Shared.Authorization;
using Application.Shared.Data;
using Application.Shared.Models.Data;
using Application.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Application.Controllers;

/// <summary>
/// Manages the named HTTP credentials that <c>source.api</c> and <c>destination.api</c> pipeline steps use.
/// <para>
/// DATA_ADMIN, like the rest of the pipeline surface. The secret is write-only across this whole controller:
/// it goes in on create/update and is never returned, not even masked — a masked value invites a client to
/// round-trip it back and overwrite the real one with asterisks.
/// </para>
/// </summary>
[Route("api/api-credentials")]
[ApiController]
[Authorize(Policy = PolicyNames.DataAdminAccess)]
public class ApiCredentialsController(
    ApplicationDbContext db,
    ICredentialProtector protector) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ApiCredentialDto>>> GetAll()
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var rows = await db.ApiCredential.AsNoTracking()
            .Where(c => c.CompanyId == companyId)
            .OrderBy(c => c.Name)
            .ToListAsync(HttpContext.RequestAborted);

        return Ok(rows.Select(ToDto));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiCredentialDto>> Get(string id)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var row = await db.ApiCredential.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == id, HttpContext.RequestAborted);

        return row is null ? NotFound() : Ok(ToDto(row));
    }

    [HttpPost]
    public async Task<ActionResult<ApiCredentialDto>> Create([FromBody] ApiCredentialSaveRequest request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;

        var invalid = Validate(request, isCreate: true);
        if (invalid is not null) return BadRequest(invalid);

        var name = request.Name!.Trim();

        if (await db.ApiCredential.AnyAsync(
                c => c.CompanyId == companyId && c.Name == name, HttpContext.RequestAborted))
        {
            return BadRequest($"A credential called '{name}' already exists.");
        }

        var row = new ApiCredential
        {
            CompanyId = companyId,
            Name = name,
            CreatedBy = userId,
            CreatedOn = DateTime.Now,
            ModifiedOn = DateTime.Now
        };

        Apply(row, request, userId);

        db.ApiCredential.Add(row);
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok(ToDto(row));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiCredentialDto>> Update(
        string id, [FromBody] ApiCredentialSaveRequest request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;

        var row = await db.ApiCredential
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == id, HttpContext.RequestAborted);

        if (row is null) return NotFound();

        var invalid = Validate(request, isCreate: false);
        if (invalid is not null) return BadRequest(invalid);

        var name = request.Name!.Trim();

        if (await db.ApiCredential.AnyAsync(
                c => c.CompanyId == companyId && c.Name == name && c.Id != id, HttpContext.RequestAborted))
        {
            return BadRequest($"A credential called '{name}' already exists.");
        }

        row.Name = name;
        Apply(row, request, userId);

        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(ToDto(row));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;

        var row = await db.ApiCredential
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == id, HttpContext.RequestAborted);

        if (row is null) return NotFound();

        // No FK from pipeline to credential — a graph references it by name in JSON — so nothing stops this
        // delete at the database level. Say which pipelines will break rather than letting them fail at 3am.
        var users = await UsedByAsync(companyId, row.Name, HttpContext.RequestAborted);
        if (users.Count > 0)
        {
            return BadRequest(
                $"'{row.Name}' is used by {string.Join(", ", users)}. "
                + "Repoint or delete those steps first.");
        }

        db.ApiCredential.Remove(row);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>
    /// Which pipelines reference this credential by name. A substring test over the stored graph would
    /// match a similarly-named credential, so this parses and checks the actual config value.
    /// </summary>
    private async Task<List<string>> UsedByAsync(string companyId, string name, CancellationToken ct)
    {
        var pipelines = await db.Pipeline.AsNoTracking()
            .Where(p => p.CompanyId == companyId)
            .Select(p => new { p.Name, p.GraphJson })
            .ToListAsync(ct);

        var used = new List<string>();

        foreach (var pipeline in pipelines)
        {
            if (string.IsNullOrWhiteSpace(pipeline.GraphJson)) continue;

            try
            {
                if (JsonNode.Parse(pipeline.GraphJson!) is not JsonObject graph) continue;
                if (graph["nodes"] is not JsonArray nodes) continue;

                var hit = nodes.OfType<JsonObject>().Any(node =>
                    node["config"] is JsonObject config
                    && string.Equals(config["credential"]?.ToString(), name, StringComparison.Ordinal));

                if (hit) used.Add(pipeline.Name);
            }
            catch (System.Text.Json.JsonException)
            {
                // An unparseable graph cannot be shown to reference this credential; the pipeline itself is
                // already broken and its own validation will say so.
            }
        }

        return used;
    }

    private void Apply(ApiCredential row, ApiCredentialSaveRequest request, string userId)
    {
        row.Description = request.Description?.Trim();
        row.BaseUrl = request.BaseUrl?.Trim();
        row.AuthType = request.AuthType ?? ApiAuthTypes.None;
        row.Username = request.Username?.Trim();
        row.HeaderName = request.HeaderName?.Trim();
        row.QueryParamName = request.QueryParamName?.Trim();
        row.FormFieldName = request.FormFieldName?.Trim();
        row.TokenUrl = request.TokenUrl?.Trim();
        row.TokenFieldsJson = request.TokenFieldsJson?.Trim();
        row.ExtraHeadersJson = string.IsNullOrWhiteSpace(request.ExtraHeadersJson)
            ? null
            : request.ExtraHeadersJson!.Trim();
        row.AllowWrite = request.AllowWrite;
        row.IsEnabled = request.IsEnabled;
        row.TimeoutSeconds = request.TimeoutSeconds;
        row.ModifiedBy = userId;
        row.ModifiedOn = DateTime.Now;

        // An empty secret on update means "leave it alone". Without that rule, saving a change to the base
        // URL from a form that never received the secret would silently blank the token.
        if (!string.IsNullOrEmpty(request.Secret))
            row.SecretEncrypted = protector.Encrypt(request.Secret!);

        // Switching to an auth type that needs nothing should not keep a stale secret around.
        if (row.AuthType == ApiAuthTypes.None)
            row.SecretEncrypted = null;
    }

    private static string? Validate(ApiCredentialSaveRequest request, bool isCreate)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return "A name is required.";

        var authType = request.AuthType ?? ApiAuthTypes.None;

        if (!ApiAuthTypes.All.Contains(authType))
            return $"'{authType}' is not a supported auth type.";

        if (isCreate && ApiAuthTypes.NeedsSecret(authType) && string.IsNullOrEmpty(request.Secret))
            return "This auth type needs a secret.";

        if (authType == ApiAuthTypes.Header && string.IsNullOrWhiteSpace(request.HeaderName))
            return "Header auth needs the header name, for example X-Api-Key.";

        if (authType == ApiAuthTypes.QueryParam && string.IsNullOrWhiteSpace(request.QueryParamName))
            return "Query-parameter auth needs the parameter name.";

        if (authType == ApiAuthTypes.FormField && string.IsNullOrWhiteSpace(request.FormFieldName))
            return "Form-field auth needs the field name, for example client_secret.";

        if (authType == ApiAuthTypes.OAuth2)
        {
            if (string.IsNullOrWhiteSpace(request.TokenUrl))
                return "OAuth2 needs the token URL.";

            if (!Uri.TryCreate(request.TokenUrl, UriKind.Absolute, out var tokenUri))
                return "The token URL must be an absolute URL.";

            if (tokenUri.Scheme != Uri.UriSchemeHttp && tokenUri.Scheme != Uri.UriSchemeHttps)
                return "The token URL must be http or https.";

            // Checked at save so a typo surfaces here rather than as a failed token request at 3am. A
            // malformed value would otherwise be swallowed by the fetcher, which cannot fail the save.
            if (!string.IsNullOrWhiteSpace(request.TokenFieldsJson))
            {
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(request.TokenFieldsJson!)
                        is not System.Text.Json.Nodes.JsonObject)
                    {
                        return "The token fields must be a JSON object.";
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return "The token fields are not valid JSON.";
                }
            }
        }

        if (authType == ApiAuthTypes.Basic && string.IsNullOrWhiteSpace(request.Username))
            return "Basic auth needs a username.";

        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri))
                return "The base URL must be an absolute URL.";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return "The base URL must be http or https.";
        }

        // Validated here rather than at request time: a malformed header blob would otherwise surface as a
        // silently header-less request in the middle of a nightly run.
        if (!string.IsNullOrWhiteSpace(request.ExtraHeadersJson))
        {
            try
            {
                if (JsonNode.Parse(request.ExtraHeadersJson!) is not JsonObject)
                    return "Extra headers must be a JSON object, for example {\"Accept\": \"application/json\"}.";
            }
            catch (System.Text.Json.JsonException ex)
            {
                return $"Extra headers is not valid JSON: {ex.Message}";
            }
        }

        if (request.TimeoutSeconds is int t && (t < 1 || t > 3600))
            return "The timeout must be between 1 and 3600 seconds.";

        return null;
    }

    private static ApiCredentialDto ToDto(ApiCredential row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        BaseUrl = row.BaseUrl,
        AuthType = row.AuthType,
        Username = row.Username,
        HeaderName = row.HeaderName,
        QueryParamName = row.QueryParamName,
        FormFieldName = row.FormFieldName,
        TokenUrl = row.TokenUrl,
        TokenFieldsJson = row.TokenFieldsJson,
        ExtraHeadersJson = row.ExtraHeadersJson,
        AllowWrite = row.AllowWrite,
        IsEnabled = row.IsEnabled,
        TimeoutSeconds = row.TimeoutSeconds,
        HasSecret = !string.IsNullOrEmpty(row.SecretEncrypted),
        ModifiedOn = row.ModifiedOn
    };

    private bool TryContext(out string companyId, out string userId, out ActionResult? failure)
    {
        companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? string.Empty;
        userId = Request.Headers["UserId"].ToString();

        if (string.IsNullOrWhiteSpace(companyId))
        {
            failure = BadRequest("Company ID is required");
            return false;
        }
        if (string.IsNullOrWhiteSpace(userId))
        {
            failure = BadRequest("User ID is required in headers");
            return false;
        }
        if (!User.HasCompanyRole(companyId, "DATA_ADMIN"))
        {
            failure = Forbid();
            return false;
        }

        failure = null;
        return true;
    }
}

/// <summary>What the client sees. Deliberately has no secret field at all.</summary>
public sealed class ApiCredentialDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? BaseUrl { get; set; }
    public string? AuthType { get; set; }
    public string? Username { get; set; }
    public string? HeaderName { get; set; }
    public string? QueryParamName { get; set; }

    public string? FormFieldName { get; set; }

    public string? TokenUrl { get; set; }

    public string? TokenFieldsJson { get; set; }
    public string? ExtraHeadersJson { get; set; }
    public bool AllowWrite { get; set; }
    public bool IsEnabled { get; set; }
    public int? TimeoutSeconds { get; set; }

    /// <summary>Whether a secret is stored, so the form can say "leave empty to keep it".</summary>
    public bool HasSecret { get; set; }

    public DateTime? ModifiedOn { get; set; }
}

public sealed class ApiCredentialSaveRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? BaseUrl { get; set; }
    public string? AuthType { get; set; }
    public string? Username { get; set; }

    /// <summary>Write-only. Empty on update means "keep the stored secret".</summary>
    public string? Secret { get; set; }

    public string? HeaderName { get; set; }
    public string? QueryParamName { get; set; }

    public string? FormFieldName { get; set; }

    public string? TokenUrl { get; set; }

    public string? TokenFieldsJson { get; set; }
    public string? ExtraHeadersJson { get; set; }
    public bool AllowWrite { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int? TimeoutSeconds { get; set; }
}

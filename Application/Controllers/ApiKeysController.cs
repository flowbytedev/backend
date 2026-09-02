using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Application.Shared.Authorization;
using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Admin-facing management of external-access API keys. Cookie/OIDC authenticated like the rest of
/// the app; key creation/rotation is restricted to company admins because a key grants data access.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = PolicyNames.DatasetsAccess)]
public class ApiKeysController : ControllerBase
{
    private readonly IApiKeyService _apiKeyService;

    public ApiKeysController(IApiKeyService apiKeyService) => _apiKeyService = apiKeyService;

    // GET: api/ApiKeys
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ApiKeyDto>>> GetKeys()
    {
        var companyId = CompanyId();
        if (string.IsNullOrWhiteSpace(companyId)) return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId)) return Forbid();

        return Ok(await _apiKeyService.GetKeysAsync(companyId));
    }

    // POST: api/ApiKeys  → returns the plaintext key exactly once.
    [HttpPost]
    public async Task<ActionResult<CreateApiKeyResult>> CreateKey([FromBody] CreateApiKeyRequest request)
    {
        var companyId = CompanyId();
        if (string.IsNullOrWhiteSpace(companyId)) return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId)) return Forbid();

        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("A name is required");
        if (request.Scopes == null || !request.Scopes.Any())
            return BadRequest("At least one dataset/table grant is required");

        var createdBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var result = await _apiKeyService.CreateKeyAsync(companyId, request, createdBy);
        return Ok(result);
    }

    // PUT: api/ApiKeys/{id}
    [HttpPut("{id}")]
    public async Task<ActionResult<ApiKeyDto>> UpdateKey(string id, [FromBody] UpdateApiKeyRequest request)
    {
        var companyId = CompanyId();
        if (string.IsNullOrWhiteSpace(companyId)) return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId)) return Forbid();

        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("A name is required");
        if (request.Scopes == null || !request.Scopes.Any())
            return BadRequest("At least one dataset/table grant is required. Revoke the key to cut off access entirely.");

        var updated = await _apiKeyService.UpdateKeyAsync(companyId, id, request);
        if (updated == null) return NotFound();
        return Ok(updated);
    }

    // POST: api/ApiKeys/{id}/rotate  → issues a new secret, returned exactly once. Name, expiry
    // and grants are untouched, so callers only have to swap the secret they send.
    [HttpPost("{id}/rotate")]
    public async Task<ActionResult<CreateApiKeyResult>> RotateKey(string id)
    {
        var companyId = CompanyId();
        if (string.IsNullOrWhiteSpace(companyId)) return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId)) return Forbid();

        try
        {
            var result = await _apiKeyService.RotateKeyAsync(companyId, id);
            if (result == null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // POST: api/ApiKeys/{id}/revoke
    [HttpPost("{id}/revoke")]
    public async Task<IActionResult> RevokeKey(string id)
    {
        var companyId = CompanyId();
        if (string.IsNullOrWhiteSpace(companyId)) return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId)) return Forbid();

        if (!await _apiKeyService.RevokeKeyAsync(companyId, id)) return NotFound();
        return NoContent();
    }

    // DELETE: api/ApiKeys/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteKey(string id)
    {
        var companyId = CompanyId();
        if (string.IsNullOrWhiteSpace(companyId)) return BadRequest("Company ID is required");
        if (!User.HasCompanyRole(companyId)) return Forbid();

        if (!await _apiKeyService.DeleteKeyAsync(companyId, id)) return NotFound();
        return NoContent();
    }

    private string CompanyId() => Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
}

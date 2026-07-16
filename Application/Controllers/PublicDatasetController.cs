using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// External API (API-key + X-User-Id) exposing the datasets and per-user access the chat app needs.
/// Routes match the chat client exactly: <c>GET api/dataset/user</c>, plus per-dataset access/RLS/creds.
/// </summary>
[Route("api/dataset")]
public class PublicDatasetController : PublicApiControllerBase
{
    private readonly IPublicDatasetApiService _api;

    public PublicDatasetController(IPublicDatasetApiService api) => _api = api;

    /// <summary>Datasets the acting user can access (visibility scoped by the user's grants).</summary>
    [HttpGet("user")]
    public async Task<ActionResult<List<PublicDatasetDto>>> GetUserDatasets(CancellationToken ct)
    {
        if (!TryGetContext(out var companyId, out var userId, out var error)) return error!;
        return Ok(await _api.GetUserDatasetsAsync(companyId, userId, ct));
    }

    /// <summary>The user's table access (with nested per-column grants) for a dataset.</summary>
    [HttpGet("{datasetId}/table-access")]
    public async Task<ActionResult<List<UserTableAccessDto>>> GetTableAccess(string datasetId, CancellationToken ct)
    {
        if (!TryGetContext(out var companyId, out var userId, out var error)) return error!;
        return Ok(await _api.GetUserTableAccessAsync(companyId, userId, datasetId, ct));
    }

    /// <summary>The user's flat per-column access grants for a dataset.</summary>
    [HttpGet("{datasetId}/column-access")]
    public async Task<ActionResult<List<UserColumnAccessDto>>> GetColumnAccess(string datasetId, CancellationToken ct)
    {
        if (!TryGetContext(out var companyId, out var userId, out var error)) return error!;
        return Ok(await _api.GetUserColumnAccessAsync(companyId, userId, datasetId, ct));
    }

    /// <summary>Row-level security filters for the user + dataset (allowedValues is a JSON string).</summary>
    [HttpGet("{datasetId}/rls")]
    public async Task<ActionResult<List<UserRlsFilterDto>>> GetRls(string datasetId, CancellationToken ct)
    {
        if (!TryGetContext(out var companyId, out var userId, out var error)) return error!;
        return Ok(await _api.GetUserRlsAsync(companyId, userId, datasetId, ct));
    }

    /// <summary>Decrypted source credentials for an external dataset (server-to-server; External only).</summary>
    [HttpGet("{datasetId}/credentials")]
    public async Task<ActionResult<DatasetCredentialDto>> GetCredentials(string datasetId, CancellationToken ct)
    {
        if (!TryGetContext(out var companyId, out var userId, out var error)) return error!;
        var cred = await _api.GetCredentialAsync(companyId, userId, datasetId, ct);
        return cred == null ? NotFound() : Ok(cred);
    }
}

using Application.Shared.Models.Data;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// External API (API-key + X-User-Id) exposing a dataset's data catalog (tables + columns, trimmed to the
/// acting user's table/column access). Route matches the chat client: <c>GET api/datacatalog/{datasetId}</c>.
/// </summary>
[Route("api/datacatalog")]
public class PublicDataCatalogController : PublicApiControllerBase
{
    private readonly IPublicDatasetApiService _api;

    public PublicDataCatalogController(IPublicDatasetApiService api) => _api = api;

    [HttpGet("{datasetId}")]
    public async Task<ActionResult<DataCatalogDto>> GetUserDataCatalog(string datasetId, CancellationToken ct)
    {
        if (!TryGetContext(out var companyId, out var userId, out var error)) return error!;
        var catalog = await _api.GetDataCatalogAsync(companyId, userId, datasetId, ct);
        return catalog == null ? NotFound($"Dataset '{datasetId}' not found or not accessible.") : Ok(catalog);
    }
}

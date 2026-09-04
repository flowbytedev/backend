using Application.Shared.Authorization;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Application.Controllers;

/// <summary>
/// The read-side connection to an external database, managed from the data module's Connections page.
/// <para>
/// A second door onto the same rows <c>DatabaseTableController</c> already exposes, and deliberately so:
/// that one lives in the status module behind <c>StatusRead</c>/<c>StatusWrite</c>, which is the right bar
/// for somebody curating a dependency map and the wrong one for somebody wiring up a pipeline. A DATA_ADMIN
/// already manages API credentials and database <em>write</em> logins on this page — strictly more
/// dangerous things — so gating a read connection behind an incident-management role only meant they had to
/// ask somebody else.
/// </para>
/// <para>
/// The storage, the encryption and the validation are all <see cref="IDatabaseTableService"/>'s; nothing is
/// duplicated here but the authorization and the listing.
/// </para>
/// </summary>
[Route("api/database-connections")]
[ApiController]
[Authorize(Policy = PolicyNames.DataAdminAccess)]
public class DatabaseConnectionsController(
    StatusDbContext status,
    IDatabaseTableService databases,
    IMonitoredAssetService entities) : ControllerBase
{
    /// <summary>Every database entity for the company, each with its connection if one is configured.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DatabaseConnectionRowDto>>> GetAll()
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        var databaseEntities = await status.MonitoredAssets.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.EntityType == AssetType.Database)
            .Select(a => new { a.Id, a.Name, a.Description })
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        var connections = await status.DatabaseConnections.AsNoTracking()
            .Where(c => c.CompanyId == companyId)
            .ToListAsync(ct);

        var byEntity = connections
            .Where(c => !string.IsNullOrEmpty(c.EntityId))
            .GroupBy(c => c.EntityId!)
            .ToDictionary(g => g.Key, g => g.First());

        return Ok(databaseEntities.Select(entity =>
        {
            byEntity.TryGetValue(entity.Id ?? string.Empty, out var connection);

            return new DatabaseConnectionRowDto
            {
                EntityId = entity.Id,
                EntityName = entity.Name,
                Description = entity.Description,
                Configured = connection is not null,
                DatabaseType = connection?.DatabaseType ?? DataSourceType.SQLServer,
                Host = connection?.Host,
                Port = connection?.Port ?? 0,
                DatabaseName = connection?.DatabaseName,
                Username = connection?.Username,
                FilePath = connection?.FilePath,
                UseSsl = connection?.UseSsl ?? false,
                // Never the secret itself. The whole page is built on "blank means keep what is stored".
                HasSecret = !string.IsNullOrEmpty(connection?.SecretEncrypted),
                ModifiedOn = connection?.ModifiedOn
            };
        }));
    }

    /// <summary>
    /// Registers a new database entity and saves its connection in one call.
    /// <para>
    /// One call rather than two because the pair is only useful together: an entity with no connection
    /// cannot back a dataset or a pipeline step, and a half-finished create is a row somebody has to find
    /// and clean up. If the connection cannot be saved the entity is removed again, so a failed attempt
    /// leaves nothing behind.
    /// </para>
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<DatabaseConnectionRowDto>> Create(
        [FromBody] CreateDatabaseConnectionRequest request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest("A name is required.");

        var name = request.Name.Trim();

        var clash = await status.MonitoredAssets.AsNoTracking().AnyAsync(
            a => a.CompanyId == companyId && !a.IsDeleted
                 && a.EntityType == AssetType.Database && a.Name == name, ct);

        if (clash)
            return BadRequest($"A database called '{name}' already exists in this company.");

        var entity = await entities.CreateEntityAsync(new MonitoredAsset
        {
            CompanyId = companyId,
            Name = name,
            Description = request.Description,
            EntityType = AssetType.Database,
            Group = string.IsNullOrWhiteSpace(request.Group) ? "Default" : request.Group!.Trim(),
            IsActive = true,
            CreatedBy = userId,
        });

        try
        {
            await databases.SaveConnectionAsync(
                entity.Id, companyId, request.Connection ?? new DatabaseConnectionRequest(), userId, ct);
        }
        catch (Exception ex)
        {
            // Rolled back by hand: there is no ambient transaction across the two services, and an entity
            // with no connection is exactly the orphan this endpoint exists to avoid.
            try { await entities.DeleteEntityAsync(entity.Id); } catch { /* best effort */ }
            return BadRequest($"The database was not created: {ex.Message}");
        }

        var saved = await databases.GetConnectionAsync(entity.Id, companyId, ct);

        return Ok(new DatabaseConnectionRowDto
        {
            EntityId = entity.Id,
            EntityName = entity.Name,
            Description = entity.Description,
            Configured = saved is not null,
            DatabaseType = saved?.DatabaseType ?? DataSourceType.SQLServer,
            Host = saved?.Host,
            Port = saved?.Port ?? 0,
            DatabaseName = saved?.DatabaseName,
            Username = saved?.Username,
            FilePath = saved?.FilePath,
            UseSsl = saved?.UseSsl ?? false,
            HasSecret = saved?.HasSecret ?? false,
        });
    }

    [HttpPut("{entityId}")]
    public async Task<ActionResult<DatabaseConnectionDto>> Save(
        string entityId, [FromBody] DatabaseConnectionRequest request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        if (!await IsCompanyDatabaseAsync(entityId, companyId, ct))
            return NotFound("No database entity with that id belongs to this company.");

        try
        {
            return Ok(await databases.SaveConnectionAsync(entityId, companyId, request, userId, ct));
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{entityId}")]
    public async Task<IActionResult> Delete(string entityId)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        if (!await IsCompanyDatabaseAsync(entityId, companyId, ct))
            return NotFound("No database entity with that id belongs to this company.");

        return await databases.DeleteConnectionAsync(entityId, companyId, ct) ? NoContent() : NotFound();
    }

    [HttpPost("{entityId}/test")]
    public async Task<ActionResult<DatabaseConnectionTestResult>> Test(string entityId)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        if (!await IsCompanyDatabaseAsync(entityId, companyId, ct))
            return NotFound("No database entity with that id belongs to this company.");

        return Ok(await databases.TestConnectionAsync(entityId, companyId, ct));
    }

    private Task<bool> IsCompanyDatabaseAsync(string entityId, string companyId, CancellationToken ct) =>
        status.MonitoredAssets.AsNoTracking().AnyAsync(
            a => a.Id == entityId && a.CompanyId == companyId && !a.IsDeleted
                 && a.EntityType == AssetType.Database, ct);

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

/// <summary>One database entity plus its connection, as the Connections page lists them.</summary>
public sealed class DatabaseConnectionRowDto
{
    public string? EntityId { get; set; }
    public string? EntityName { get; set; }
    public string? Description { get; set; }

    /// <summary>False for a database entity that has no connection yet — it cannot back a dataset.</summary>
    public bool Configured { get; set; }

    public DataSourceType DatabaseType { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? DatabaseName { get; set; }
    public string? Username { get; set; }
    public string? FilePath { get; set; }
    public bool UseSsl { get; set; }
    public bool HasSecret { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>The entity and its connection together — see <see cref="DatabaseConnectionsController.Create"/>.</summary>
public sealed class CreateDatabaseConnectionRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Group { get; set; }
    public DatabaseConnectionRequest? Connection { get; set; }
}

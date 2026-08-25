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
/// The write-side credential a pipeline needs to load data into an external (database-backed) dataset.
/// <para>
/// A separate controller from <c>DatabaseAdminController</c> even though both manage a credential on the
/// same entity, because they answer to different people: that one is DBA tooling behind the DatabaseAdmin
/// policy, this is a data-integration setting behind DATA_ADMIN. Merging them would mean whoever can run a
/// pipeline can also reach CREATE LOGIN.
/// </para>
/// <para>
/// One row per entity, so save is an upsert. The password is write-only: it goes in here and is never
/// returned.
/// </para>
/// </summary>
[Route("api/database-write-credentials")]
[ApiController]
[Authorize(Policy = PolicyNames.DataAdminAccess)]
public class DatabaseWriteCredentialsController(
    StatusDbContext status,
    ICredentialProtector protector) : ControllerBase
{
    /// <summary>Every database entity for the company, each with its write credential if one exists.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DatabaseWriteCredentialDto>>> GetAll()
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        var databases = await status.MonitoredAssets.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.EntityType == AssetType.Database)
            .Select(a => new { a.Id, a.Name })
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        var credentials = await status.DatabaseWriteCredentials.AsNoTracking()
            .Where(c => c.CompanyId == companyId)
            .ToListAsync(ct);

        var byEntity = credentials
            .Where(c => !string.IsNullOrEmpty(c.EntityId))
            .GroupBy(c => c.EntityId!)
            .ToDictionary(g => g.Key, g => g.First());

        return Ok(databases.Select(d =>
        {
            byEntity.TryGetValue(d.Id ?? string.Empty, out var credential);

            return new DatabaseWriteCredentialDto
            {
                EntityId = d.Id,
                EntityName = d.Name,
                Configured = credential is not null,
                Username = credential?.Username,
                AllowCreateTable = credential?.AllowCreateTable ?? false,
                HasSecret = !string.IsNullOrEmpty(credential?.SecretEncrypted),
                ModifiedOn = credential?.ModifiedOn
            };
        }));
    }

    [HttpPut("{entityId}")]
    public async Task<ActionResult<DatabaseWriteCredentialDto>> Save(
        string entityId, [FromBody] DatabaseWriteCredentialSaveRequest request)
    {
        if (!TryContext(out var companyId, out var userId, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        var entity = await status.MonitoredAssets.AsNoTracking().FirstOrDefaultAsync(
            a => a.Id == entityId && a.CompanyId == companyId && !a.IsDeleted && a.EntityType == AssetType.Database, ct);

        if (entity is null)
            return NotFound("No database entity with that id belongs to this company.");

        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest("A username is required.");

        var existing = await status.DatabaseWriteCredentials.FirstOrDefaultAsync(
            c => c.EntityId == entityId && c.CompanyId == companyId, ct);

        if (existing is null && string.IsNullOrEmpty(request.Secret))
            return BadRequest("A password is required when adding a credential.");

        var row = existing ?? new DatabaseWriteCredential
        {
            EntityId = entityId,
            CompanyId = companyId,
            CreatedBy = userId,
            CreatedOn = DateTime.Now
        };

        row.Username = request.Username!.Trim();
        row.AllowCreateTable = request.AllowCreateTable;
        row.ModifiedBy = userId;
        row.ModifiedOn = DateTime.Now;

        // Empty means "keep the stored password" — otherwise toggling allow-create-table from a form that
        // never received the password would wipe it.
        if (!string.IsNullOrEmpty(request.Secret))
            row.SecretEncrypted = protector.Encrypt(request.Secret!);

        if (existing is null) status.DatabaseWriteCredentials.Add(row);

        await status.SaveChangesAsync(ct);

        return Ok(new DatabaseWriteCredentialDto
        {
            EntityId = entityId,
            EntityName = entity.Name,
            Configured = true,
            Username = row.Username,
            AllowCreateTable = row.AllowCreateTable,
            HasSecret = !string.IsNullOrEmpty(row.SecretEncrypted),
            ModifiedOn = row.ModifiedOn
        });
    }

    [HttpDelete("{entityId}")]
    public async Task<IActionResult> Delete(string entityId)
    {
        if (!TryContext(out var companyId, out _, out var failure)) return failure!;
        var ct = HttpContext.RequestAborted;

        var row = await status.DatabaseWriteCredentials.FirstOrDefaultAsync(
            c => c.EntityId == entityId && c.CompanyId == companyId, ct);

        if (row is null) return NotFound();

        status.DatabaseWriteCredentials.Remove(row);
        await status.SaveChangesAsync(ct);
        return NoContent();
    }

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

public sealed class DatabaseWriteCredentialDto
{
    public string? EntityId { get; set; }
    public string? EntityName { get; set; }

    /// <summary>False for a database that has no write credential — most of them.</summary>
    public bool Configured { get; set; }

    public string? Username { get; set; }
    public bool AllowCreateTable { get; set; }
    public bool HasSecret { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

public sealed class DatabaseWriteCredentialSaveRequest
{
    public string? Username { get; set; }

    /// <summary>Write-only. Empty on update keeps the stored password.</summary>
    public string? Secret { get; set; }

    public bool AllowCreateTable { get; set; }
}

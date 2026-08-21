using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Models;

/// <summary>
/// Read-only view of dbo.application_user_access in the shared `identity` database. The composite
/// key mirrors the identity app's own mapping.
///
/// There is intentionally no ApplicationUser navigation: it would add a second relationship into
/// the Identity model for no benefit, and backend's snake_case convention dereferences
/// PropertyInfo without a null check, so any shadow property would break the whole model build.
/// ApplicationId is declared explicitly for the same reason.
/// </summary>
[Table("application_user_access")]
[PrimaryKey(nameof(ApplicationUserId), nameof(ApplicationId))]
public class AppRegistryUserAccess
{
    public string ApplicationUserId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }

    public AppRegistryApplication? Application { get; set; }
}

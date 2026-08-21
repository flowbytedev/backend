using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Application.Shared.Models;

/// <summary>
/// Read-only view of dbo.application in the shared `identity` database. That table is owned and
/// written by the identity app; this app only reads it, to gate login and draw the app launcher.
///
/// Deliberately NOT named `Application` (it would collide with the root namespace) and
/// deliberately NOT derived from BaseModel: BaseModel is a different class in each repo -- in
/// backend `IsDeleted` is commented out -- so inheriting it would not compile everywhere.
/// [Table] pins the physical name so it does not depend on the DbSet property name; the
/// snake_case convention leaves an already-lowercase name untouched.
/// </summary>
[Table("application")]
public class AppRegistryApplication
{
    [Key]
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    [MaxLength(500)] public string? Url { get; set; }
    [MaxLength(100)] public string? Icon { get; set; }
    [MaxLength(20)]  public string? Color { get; set; }
    [MaxLength(300)] public string? Description { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
}

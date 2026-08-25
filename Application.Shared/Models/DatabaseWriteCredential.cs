using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Application.Shared.Models;

/// <summary>
/// Credential used to WRITE into a Database-type entity — the destination side of an ETL pipeline.
/// <para>
/// This is the third credential on an entity, and the reason is the same one that separated the first two.
/// <see cref="DatabaseConnection"/> is the least-privilege reader: it is built with read-only intent
/// everywhere (ApplicationIntent, SET SESSION READ ONLY) and its account is not expected to hold INSERT.
/// <see cref="DatabaseAdminCredential"/> is the opposite extreme — a DBA account for CREATE LOGIN and
/// ALTER ROLE. Loading a nightly table with either would be wrong: the first cannot, and the second is far
/// too much authority for the job.
/// </para>
/// <para>
/// Like the admin credential, this row only overrides <i>who</i> connects. Host, port, catalog and SSL are
/// still read from the entity's <see cref="DatabaseConnection"/>, so there is one source of truth for where
/// the server is. One row per entity; the password is encrypted at rest and never serialized to the browser.
/// </para>
/// </summary>
[Table("entity_database_write_credential")]
public class DatabaseWriteCredential : BaseModel
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string EntityId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Username { get; set; }

    /// <summary>Encrypted password. Never returned to the client.</summary>
    [JsonIgnore]
    public string? SecretEncrypted { get; set; }

    /// <summary>
    /// Whether a pipeline may CREATE a table through this credential. Off by default: issuing DDL inside
    /// someone else's database is a decision an operator makes once, not something a pipeline author enables
    /// from the canvas.
    /// </summary>
    public bool AllowCreateTable { get; set; }

    [JsonIgnore]
    [ForeignKey(nameof(EntityId))]
    public virtual MonitoredAsset? Entity { get; set; }
}

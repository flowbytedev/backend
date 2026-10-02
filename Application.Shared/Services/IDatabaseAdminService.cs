using Application.Shared.Enums;
using Application.Shared.Models;

namespace Application.Shared.Services;

/// <summary>
/// Administrative operations against the external databases registered as Database-type entities:
/// space usage and user provisioning.
/// <para>
/// Space checks use the entity's ordinary (least-privilege) <see cref="DatabaseConnection"/>. Every user
/// operation requires a separate <see cref="DatabaseAdminCredential"/> and is refused outright when none
/// is stored — the read-only credential is never silently escalated.
/// </para>
/// </summary>
public interface IDatabaseAdminService
{
    /// <summary>Every Database-type entity for the company, including those with no connection configured yet.</summary>
    Task<List<DatabaseAdminEntityDto>> GetDatabaseEntitiesAsync(string companyId, CancellationToken ct = default);

    Task<DatabaseAdminCredentialDto?> GetAdminCredentialAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Creates or updates the admin credential. A blank secret keeps the stored one.</summary>
    Task<DatabaseAdminCredentialDto> SaveAdminCredentialAsync(string entityId, string companyId, DatabaseAdminCredentialRequest request, string? modifiedBy, CancellationToken ct = default);

    Task<bool> DeleteAdminCredentialAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Space usage for the database, using the read-only credential. Never throws — errors land in the DTO.</summary>
    Task<DatabaseSizeDto> GetSizeAsync(string entityId, string companyId, CancellationToken ct = default);

    /// <summary>Principals on the database. Requires the admin credential. Never throws — errors land in the DTO.</summary>
    Task<DatabaseUserListDto> ListUsersAsync(string entityId, string companyId, CancellationToken ct = default);

    Task<DatabaseUserOperationResult> CreateUserAsync(string entityId, string companyId, CreateDatabaseUserRequest request, CancellationToken ct = default);

    Task<DatabaseUserOperationResult> ResetPasswordAsync(string entityId, string companyId, ResetDatabaseUserPasswordRequest request, CancellationToken ct = default);

    Task<DatabaseUserOperationResult> DropUserAsync(string entityId, string companyId, DropDatabaseUserRequest request, CancellationToken ct = default);

    /// <summary>What the given engine supports. DuckDB has no user accounts, so only the size check applies.</summary>
    static DatabaseAdminCapabilities CapabilitiesFor(DataSourceType type) =>
        type == DataSourceType.MongoDB
            ? new DatabaseAdminCapabilities
            {
                UnsupportedReason = "MongoDB connections are read-only pipeline sources. Manage its users in MongoDB itself (Atlas or mongosh)."
            }
        : type == DataSourceType.DuckDB
            ? new DatabaseAdminCapabilities
            {
                CanCheckSize = true,
                UnsupportedReason = "DuckDB is an embedded, file-based engine — it has no user accounts, logins or roles. Access is controlled by file permissions on the host."
            }
            : new DatabaseAdminCapabilities
            {
                CanCheckSize = true,
                CanListUsers = true,
                CanCreateUser = true,
                CanDropUser = true,
                CanResetPassword = true
            };
}

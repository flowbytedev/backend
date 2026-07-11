using Application.Shared.Models.Notebooks;
using Application.Shared.Services.Data;

namespace Application.Scheduler.Services;

/// <summary>
/// <see cref="QueryNotebookService"/> takes an <see cref="INotebookSharingService"/> constructor
/// dependency, but the real implementation needs ASP.NET Identity's <c>UserManager&lt;ApplicationUser&gt;</c>
/// (to resolve a share's email to a user) — infrastructure the scheduler process doesn't have wired up
/// (no <c>UserManagementDbContext</c> connection string here). The scheduler only ever calls
/// <see cref="IQueryNotebookService.RunAllAsync"/> with <c>isAdmin: true</c> for a scheduled run, which
/// short-circuits past every permission check that would otherwise call into this service — so
/// <see cref="GetGrantAsync"/> (the only method that code path could reach) is never actually invoked here.
/// This stub exists purely to satisfy the DI graph; sharing management genuinely isn't something the
/// scheduler process does.
/// </summary>
public class UnsupportedNotebookSharingService : INotebookSharingService
{
    public Task<List<NotebookUserDto>> GetNotebookUsersAsync(string notebookId) => Task.FromResult(new List<NotebookUserDto>());
    public Task<bool> ShareNotebookAsync(ShareNotebookRequest request, string sharedByUserId) => Task.FromResult(false);
    public Task<bool> UpdateNotebookUserTypeAsync(string notebookId, string userId, NotebookUserType userType) => Task.FromResult(false);
    public Task<bool> RemoveNotebookUserAsync(string notebookId, string userId) => Task.FromResult(false);
    public Task<NotebookUserType?> GetGrantAsync(string notebookId, string userId) => Task.FromResult<NotebookUserType?>(null);
}

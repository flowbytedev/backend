using Application.Shared.Models.Notebooks;

namespace Application.Shared.Services.Data;

public interface INotebookSharingService
{
    Task<List<NotebookUserDto>> GetNotebookUsersAsync(string notebookId);
    Task<bool> ShareNotebookAsync(ShareNotebookRequest request, string sharedByUserId);
    Task<bool> UpdateNotebookUserTypeAsync(string notebookId, string userId, NotebookUserType userType);
    Task<bool> RemoveNotebookUserAsync(string notebookId, string userId);

    /// <summary>Whether the user has an explicit per-user grant on this notebook (any type = view access;
    /// Editor = edit access too). Does not account for the notebook owner, company admins, or the
    /// company-wide IsShared flag — those are checked separately by the caller.</summary>
    Task<NotebookUserType?> GetGrantAsync(string notebookId, string userId);
}

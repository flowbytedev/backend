using Application.Shared.Models.Notebooks;

namespace Application.Shared.Services.Data;

public interface INotebookCommentService
{
    Task<List<NotebookCellComment>> GetCommentsAsync(string notebookId, string cellId);

    /// <summary><paramref name="notebookName"/> is only used for the mention-notification email — the
    /// comment itself only stores notebook/cell ids.</summary>
    Task<NotebookCellComment> AddCommentAsync(NotebookCellComment comment, string companyId, string notebookName);
    Task<bool> DeleteCommentAsync(string commentId, string userId);
    Task<NotebookCellComment?> UpdateCommentAsync(string commentId, string content, string userId);
}

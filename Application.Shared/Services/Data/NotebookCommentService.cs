using Application.Shared.Data;
using Application.Shared.Models.Notebooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services.Data;

/// <summary>Comment threads on notebook cells. Mirrors <c>CommentService</c> (dataset/table comments) —
/// same @mention parsing + email notification pattern, scoped to a notebook cell instead of a table.</summary>
public class NotebookCommentService : INotebookCommentService
{
    private readonly ApplicationDbContext _context;
    private readonly UserManagementDbContext _userContext;
    private readonly ICommentMentionNotificationService _mentionNotifier;
    private readonly ILogger<NotebookCommentService> _logger;

    public NotebookCommentService(
        ApplicationDbContext context,
        UserManagementDbContext userContext,
        ICommentMentionNotificationService mentionNotifier,
        ILogger<NotebookCommentService> logger)
    {
        _context = context;
        _userContext = userContext;
        _mentionNotifier = mentionNotifier;
        _logger = logger;
    }

    public async Task<List<NotebookCellComment>> GetCommentsAsync(string notebookId, string cellId)
    {
        return await _context.NotebookCellComment
            .Where(c => c.NotebookId == notebookId && c.CellId == cellId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }

    public async Task<NotebookCellComment> AddCommentAsync(NotebookCellComment comment, string companyId, string notebookName)
    {
        comment.Id = Guid.NewGuid().ToString();
        comment.CreatedAt = DateTime.UtcNow;

        var author = await _userContext.ApplicationUser.FirstOrDefaultAsync(u => u.Id == comment.UserId);
        comment.UserName = author?.UserName ?? author?.Email ?? "Unknown user";
        comment.UserEmail = author?.Email ?? string.Empty;

        _context.NotebookCellComment.Add(comment);
        await _context.SaveChangesAsync();

        await NotifyMentionedUsersAsync(comment, companyId, notebookName);

        return comment;
    }

    /// <summary>Emails every mentioned user (excluding the author). Never throws.</summary>
    private async Task NotifyMentionedUsersAsync(NotebookCellComment comment, string companyId, string notebookName)
    {
        try
        {
            var targetIds = comment.MentionedUserIds
                .Where(id => !string.IsNullOrWhiteSpace(id) && id != comment.UserId)
                .Distinct()
                .ToList();
            if (targetIds.Count == 0) return;

            var recipients = await _userContext.ApplicationUser
                .Where(u => targetIds.Contains(u.Id) && u.Email != null)
                .Select(u => new { u.UserName, u.Email })
                .ToListAsync();
            if (recipients.Count == 0) return;

            var cellName = await _context.QueryNotebookCell
                .Where(c => c.Id == comment.CellId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync() ?? comment.CellId;

            foreach (var r in recipients)
            {
                await _mentionNotifier.SendNotebookMentionNotificationAsync(
                    recipientEmail: r.Email!,
                    recipientName: r.UserName,
                    mentionedByName: comment.UserName,
                    notebookId: comment.NotebookId,
                    notebookName: notebookName,
                    cellName: cellName!,
                    commentContent: comment.Content,
                    companyId: companyId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NotebookCommentMention] Failed while dispatching mention notifications for comment {CommentId}.", comment.Id);
        }
    }

    public async Task<bool> DeleteCommentAsync(string commentId, string userId)
    {
        var comment = await _context.NotebookCellComment
            .FirstOrDefaultAsync(c => c.Id == commentId && c.UserId == userId);
        if (comment == null) return false;

        _context.NotebookCellComment.Remove(comment);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<NotebookCellComment?> UpdateCommentAsync(string commentId, string content, string userId)
    {
        var comment = await _context.NotebookCellComment
            .FirstOrDefaultAsync(c => c.Id == commentId && c.UserId == userId);
        if (comment == null) return null;

        comment.Content = content;
        comment.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return comment;
    }
}

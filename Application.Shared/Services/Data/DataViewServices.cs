using Application.Shared.Data;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services.Data;

public class CommentService : ICommentService
{
    private readonly ApplicationDbContext _context;
    private readonly UserManagementDbContext _userContext;
    private readonly ICommentMentionNotificationService _mentionNotifier;
    private readonly ILogger<CommentService> _logger;

    public CommentService(
        ApplicationDbContext context,
        UserManagementDbContext userContext,
        ICommentMentionNotificationService mentionNotifier,
        ILogger<CommentService> logger)
    {
        _context = context;
        _userContext = userContext;
        _mentionNotifier = mentionNotifier;
        _logger = logger;
    }

    public async Task<List<DataTableComment>> GetCommentsAsync(string datasetId, string tableName)
    {
        return await _context.Set<DataTableComment>()
            .Where(c => c.DatasetId == datasetId && c.TableName == tableName)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }

    public async Task<DataTableComment> AddCommentAsync(DataTableComment comment, string companyId)
    {
        comment.Id = Guid.NewGuid().ToString();
        comment.CreatedAt = DateTime.UtcNow;

        // Resolve the commenter's real display name/email so the comment renders correctly.
        var author = await _userContext.ApplicationUser
            .FirstOrDefaultAsync(u => u.Id == comment.UserId);
        comment.UserName = author?.UserName ?? author?.Email ?? "Unknown user";
        comment.UserEmail = author?.Email ?? string.Empty;

        _context.Set<DataTableComment>().Add(comment);
        await _context.SaveChangesAsync();

        await NotifyMentionedUsersAsync(comment, companyId);

        return comment;
    }

    /// <summary>Emails every mentioned user (excluding the author). Never throws.</summary>
    private async Task NotifyMentionedUsersAsync(DataTableComment comment, string companyId)
    {
        try
        {
            _logger.LogInformation(
                "[CommentMention] Processing comment {CommentId} (dataset {DatasetId}, table {TableName}) with {MentionCount} raw mentioned id(s): [{MentionedIds}].",
                comment.Id, comment.DatasetId, comment.TableName, comment.MentionedUserIds.Count, string.Join(", ", comment.MentionedUserIds));

            var targetIds = comment.MentionedUserIds
                .Where(id => !string.IsNullOrWhiteSpace(id) && id != comment.UserId)
                .Distinct()
                .ToList();
            if (targetIds.Count == 0)
            {
                _logger.LogInformation(
                    "[CommentMention] No mention notifications to send for comment {CommentId} — no mentioned users other than the author.",
                    comment.Id);
                return;
            }

            var recipients = await _userContext.ApplicationUser
                .Where(u => targetIds.Contains(u.Id) && u.Email != null)
                .Select(u => new { u.UserName, u.Email })
                .ToListAsync();
            if (recipients.Count == 0)
            {
                _logger.LogWarning(
                    "[CommentMention] {TargetCount} user(s) were mentioned in comment {CommentId} but none have an email on file (or the ids matched no user): [{TargetIds}].",
                    targetIds.Count, comment.Id, string.Join(", ", targetIds));
                return;
            }

            var datasetName = await _context.Dataset
                .Where(d => d.Id == comment.DatasetId)
                .Select(d => d.Name)
                .FirstOrDefaultAsync() ?? comment.DatasetId;

            _logger.LogInformation(
                "[CommentMention] Sending mention emails for comment {CommentId} to {RecipientCount} recipient(s): [{Recipients}].",
                comment.Id, recipients.Count, string.Join(", ", recipients.Select(r => r.Email)));

            foreach (var r in recipients)
            {
                await _mentionNotifier.SendMentionNotificationAsync(
                    recipientEmail: r.Email!,
                    recipientName: r.UserName,
                    mentionedByName: comment.UserName,
                    datasetId: comment.DatasetId,
                    datasetName: datasetName!,
                    tableName: comment.TableName,
                    commentContent: comment.Content,
                    companyId: companyId);
            }
        }
        catch (Exception ex)
        {
            // Mention notifications must never break comment creation.
            _logger.LogError(ex, "[CommentMention] Failed while dispatching mention notifications for comment {CommentId}.", comment.Id);
        }
    }

    public async Task<bool> DeleteCommentAsync(string commentId, string userId)
    {
        var comment = await _context.Set<DataTableComment>()
            .FirstOrDefaultAsync(c => c.Id == commentId && c.UserId == userId);
        
        if (comment == null)
            return false;
        
        _context.Set<DataTableComment>().Remove(comment);
        await _context.SaveChangesAsync();
        
        return true;
    }

    public async Task<DataTableComment?> UpdateCommentAsync(string commentId, string content, string userId)
    {
        var comment = await _context.Set<DataTableComment>()
            .FirstOrDefaultAsync(c => c.Id == commentId && c.UserId == userId);
        
        if (comment == null)
            return null;
        
        comment.Content = content;
        comment.UpdatedAt = DateTime.UtcNow;
        
        await _context.SaveChangesAsync();
        
        return comment;
    }
}

public class UserPreferencesService : IUserPreferencesService
{
    private readonly ApplicationDbContext _context;

    public UserPreferencesService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<UserColumnPreferences?> GetUserColumnPreferencesAsync(string userId, string datasetId, string tableName)
    {
        // For now, return null to indicate no saved preferences
        // In a real implementation, you'd store these in the database
        await Task.CompletedTask;
        return null;
    }

    public async Task<UserColumnPreferences> SaveUserColumnPreferencesAsync(UserColumnPreferences preferences)
    {
        // For now, just return the preferences as-is
        // In a real implementation, you'd save these to the database
        await Task.CompletedTask;
        preferences.LastModified = DateTime.UtcNow;
        return preferences;
    }
}

public class UserSearchService : IUserSearchService
{
    private readonly ApplicationDbContext _context;
    private readonly UserManagementDbContext _userContext;

    public UserSearchService(ApplicationDbContext context, UserManagementDbContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<List<UserMention>> SearchUsersAsync(string companyId, string searchTerm, int maxResults = 5)
    {
        // Restrict the search to members of the company (via CompanyMember) — never leak users from
        // other tenants. An empty search term returns the company's members (used by autocomplete on focus).
        var memberIds = await _userContext.CompanyMember
            .Where(m => m.CompanyId == companyId)
            .Select(m => m.ApplicationUserId)
            .ToListAsync();

        var query = _userContext.ApplicationUser.Where(u => memberIds.Contains(u.Id));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(u =>
                (u.UserName != null && u.UserName.Contains(searchTerm)) ||
                (u.Email != null && u.Email.Contains(searchTerm)));
        }

        return await query
            .OrderBy(u => u.UserName)
            .Take(maxResults)
            .Select(u => new UserMention
            {
                Id = u.Id,
                UserName = u.UserName ?? "",
                FullName = u.UserName ?? "", // TODO: Add FullName property to ApplicationUser
                Email = u.Email ?? ""
            })
            .ToListAsync();
    }
}

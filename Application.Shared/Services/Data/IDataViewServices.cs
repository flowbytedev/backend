using Application.Shared.Models.Data;

namespace Application.Shared.Services.Data;

public interface ICommentService
{
    Task<List<DataTableComment>> GetCommentsAsync(string datasetId, string tableName);
    Task<DataTableComment> AddCommentAsync(DataTableComment comment, string companyId);
    Task<bool> DeleteCommentAsync(string commentId, string userId);
    Task<DataTableComment?> UpdateCommentAsync(string commentId, string content, string userId);
}

public interface IUserPreferencesService
{
    Task<UserColumnPreferences?> GetUserColumnPreferencesAsync(string userId, string datasetId, string tableName);
    Task<UserColumnPreferences> SaveUserColumnPreferencesAsync(UserColumnPreferences preferences);
}

public interface IUserSearchService
{
    Task<List<UserMention>> SearchUsersAsync(string companyId, string searchTerm, int maxResults = 5);
}

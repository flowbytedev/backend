using Application.Shared.Data;
using Application.Shared.Models.Notebooks;
using Application.Shared.Models.User;
using Application.Shared.Services.Org;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

public class NotebookSharingService : INotebookSharingService
{
    private readonly ApplicationDbContext _context;
    private readonly IUserService _userService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailNotificationService _emailNotificationService;

    public NotebookSharingService(
        ApplicationDbContext context,
        IUserService userService,
        UserManager<ApplicationUser> userManager,
        IEmailNotificationService emailNotificationService)
    {
        _context = context;
        _userService = userService;
        _userManager = userManager;
        _emailNotificationService = emailNotificationService;
    }

    public async Task<List<NotebookUserDto>> GetNotebookUsersAsync(string notebookId)
    {
        var notebookUsers = await _context.NotebookUser
            .Where(nu => nu.NotebookId == notebookId)
            .Select(nu => new NotebookUserDto
            {
                UserId = nu.UserId,
                Type = nu.Type,
                CreatedAt = nu.CreatedAt
            })
            .ToListAsync();

        foreach (var nu in notebookUsers)
        {
            var user = await _userService.GetUser(nu.UserId);
            if (user != null)
            {
                nu.Email = user.Email;
                nu.UserName = user.UserName;
            }
        }

        return notebookUsers;
    }

    public async Task<bool> ShareNotebookAsync(ShareNotebookRequest request, string sharedByUserId)
    {
        try
        {
            var notebook = await _context.QueryNotebook.FirstOrDefaultAsync(n => n.Id == request.NotebookId);
            if (notebook == null) return false;

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null) return false;

            var existingShare = await _context.NotebookUser
                .FirstOrDefaultAsync(nu => nu.NotebookId == request.NotebookId && nu.UserId == user.Id);

            if (existingShare != null)
            {
                existingShare.Type = request.UserType;
                existingShare.ModifiedAt = DateTime.UtcNow;
            }
            else
            {
                _context.NotebookUser.Add(new NotebookUser
                {
                    NotebookId = request.NotebookId,
                    UserId = user.Id,
                    Type = request.UserType,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            var sharedByUser = await _userManager.FindByIdAsync(sharedByUserId);
            await _emailNotificationService.SendNotebookSharedNotificationAsync(
                request.Email,
                notebook.Id,
                notebook.Name,
                notebook.CompanyId,
                sharedByUser?.UserName ?? "Unknown User",
                request.UserType);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> UpdateNotebookUserTypeAsync(string notebookId, string userId, NotebookUserType userType)
    {
        try
        {
            var notebookUser = await _context.NotebookUser
                .FirstOrDefaultAsync(nu => nu.NotebookId == notebookId && nu.UserId == userId);
            if (notebookUser == null) return false;

            notebookUser.Type = userType;
            notebookUser.ModifiedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> RemoveNotebookUserAsync(string notebookId, string userId)
    {
        try
        {
            var notebookUser = await _context.NotebookUser
                .FirstOrDefaultAsync(nu => nu.NotebookId == notebookId && nu.UserId == userId);
            if (notebookUser == null) return false;

            _context.NotebookUser.Remove(notebookUser);
            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<NotebookUserType?> GetGrantAsync(string notebookId, string userId)
    {
        var notebookUser = await _context.NotebookUser
            .AsNoTracking()
            .FirstOrDefaultAsync(nu => nu.NotebookId == notebookId && nu.UserId == userId);
        return notebookUser?.Type;
    }
}

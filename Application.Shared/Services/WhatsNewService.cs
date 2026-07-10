using Application.Shared.Data;
using Application.Shared.Models.WhatsNew;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services;

public interface IWhatsNewService
{
    Task<WhatsNewFeedDto> GetFeedAsync(string userId, int take = 20, CancellationToken ct = default);
    Task MarkSeenAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Backs the top-bar "What's New" bell: a small global feed of feature announcements plus per-user
/// last-seen tracking. The badge count reflects items created after the user's last-seen timestamp (or
/// everything, for a user who has never opened the panel).
/// </summary>
public class WhatsNewService : IWhatsNewService
{
    private readonly ApplicationDbContext _context;

    public WhatsNewService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<WhatsNewFeedDto> GetFeedAsync(string userId, int take = 20, CancellationToken ct = default)
    {
        var lastSeen = await _context.WhatsNewSeen.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => s.LastSeenAt)
            .FirstOrDefaultAsync(ct);
        // A default DateTime (never seen) means "everything is new".

        var items = await _context.WhatsNewItem.AsNoTracking()
            .Where(i => i.IsActive)
            .OrderByDescending(i => i.CreatedAt)
            .Take(take)
            .Select(i => new WhatsNewItemDto
            {
                Id = i.Id,
                Title = i.Title,
                Description = i.Description,
                Category = i.Category,
                CreatedAt = i.CreatedAt,
                IsNew = i.CreatedAt > lastSeen
            })
            .ToListAsync(ct);

        // Counted separately from the (possibly truncated) page so it stays correct even when there are
        // more unseen items than `take`.
        var unseenCount = await _context.WhatsNewItem.AsNoTracking()
            .CountAsync(i => i.IsActive && i.CreatedAt > lastSeen, ct);

        return new WhatsNewFeedDto { Items = items, UnseenCount = unseenCount };
    }

    public async Task MarkSeenAsync(string userId, CancellationToken ct = default)
    {
        var seen = await _context.WhatsNewSeen.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (seen == null)
        {
            seen = new WhatsNewSeen { UserId = userId };
            _context.WhatsNewSeen.Add(seen);
        }
        seen.LastSeenAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }
}

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Models.WhatsNew;

/// <summary>A single "what's new" feed entry — global (not per-company), admin-authored.</summary>
public class WhatsNewItem
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(1000)]
    public string Description { get; set; } = string.Empty;

    [StringLength(40)]
    public string? Category { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

/// <summary>One row per user: the last time they opened the What's New panel.</summary>
[PrimaryKey(nameof(UserId))]
public class WhatsNewSeen
{
    [Required]
    public string UserId { get; set; } = string.Empty;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public class WhatsNewItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Category { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsNew { get; set; }
}

public class WhatsNewFeedDto
{
    public List<WhatsNewItemDto> Items { get; set; } = new();
    public int UnseenCount { get; set; }
}

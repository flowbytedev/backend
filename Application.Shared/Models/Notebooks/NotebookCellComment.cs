using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Application.Shared.Models.Notebooks;

/// <summary>Comment thread on a notebook cell. Mirrors <c>DataTableComment</c> — flat/denormalized, no FK,
/// author name/email captured at write time so rendering never needs a join.</summary>
public class NotebookCellComment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string NotebookId { get; set; } = string.Empty;

    [Required]
    public string CellId { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Content { get; set; } = string.Empty;

    public List<string> MentionedUserIds { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string UserName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
}

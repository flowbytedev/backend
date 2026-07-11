using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Models.Notebooks;

/// <summary>
/// Per-user grant on a <see cref="QueryNotebook"/> — additive to the notebook's company-wide
/// <c>IsShared</c> flag. Mirrors <c>DatasetUser</c>, minus table scoping (a notebook isn't split per
/// table). The notebook owner and company admins always have full access regardless of rows here.
/// </summary>
[PrimaryKey(nameof(NotebookId), nameof(UserId))]
public class NotebookUser
{
    [Required]
    public string NotebookId { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public NotebookUserType Type { get; set; } = NotebookUserType.Viewer;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
}

public enum NotebookUserType
{
    Editor = 0,
    Viewer = 1,
}

public class ShareNotebookRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string NotebookId { get; set; } = string.Empty;

    [Required]
    public NotebookUserType UserType { get; set; } = NotebookUserType.Viewer;
}

public class NotebookUserDto
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public NotebookUserType Type { get; set; }
    public DateTime CreatedAt { get; set; }
}

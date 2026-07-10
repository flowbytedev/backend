using System.ComponentModel.DataAnnotations;

namespace Application.Shared.Models.Data;

public class MoveTableRequest
{
    [Required]
    public string TargetDatasetId { get; set; } = string.Empty;
}

/// <summary>Result of the physical DuckDB-to-DuckDB table move. Never throws.</summary>
public class MoveTableResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>Result of the full move orchestration (data move + sharing re-point + notifications).</summary>
public class MoveTableOutcome
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int UsersNotified { get; set; }
}

using System;
using System.Collections.Generic;

namespace Application.Shared.Models.Notebooks;

public class NotebookCellDto
{
    public string Id { get; set; } = string.Empty;
    public string? DatasetId { get; set; }
    public string CellType { get; set; } = "sql";
    public string? Name { get; set; }
    public string? Sql { get; set; }
    public string? Markdown { get; set; }
    public List<string> ReferencedCellIds { get; set; } = new();
    public bool SnapshotMode { get; set; }
    public int SortOrder { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastRunError { get; set; }
    public string? LastMaterializedObject { get; set; }
}

public class QueryNotebookDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsShared { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    /// <summary>Whether the requesting user may edit/delete this notebook (creator or admin).</summary>
    public bool CanEdit { get; set; }
    /// <summary>Populated on both the list and single-notebook endpoints (even when Cells itself isn't).</summary>
    public int CellCount { get; set; }
    public List<NotebookCellDto> Cells { get; set; } = new();
}

public class SaveNotebookRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsShared { get; set; }
}

public class SaveNotebookCellRequest
{
    public string? DatasetId { get; set; }
    public string CellType { get; set; } = "sql";
    public string? Name { get; set; }
    public string? Sql { get; set; }
    public string? Markdown { get; set; }
    public List<string> ReferencedCellIds { get; set; } = new();
    public bool SnapshotMode { get; set; }
}

/// <summary>Result of running a single cell — mirrors <c>SqlQueryResult</c> plus notebook-specific fields.</summary>
public class NotebookCellRunResult
{
    public List<Data.Column> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public int RowsReturned { get; set; }
    public bool Truncated { get; set; }
    public long ElapsedMs { get; set; }
    public int RowsAffected { get; set; }
    public bool IsSelect { get; set; }
    public string? Error { get; set; }
    /// <summary>The DuckDB object name this cell materialized to, if it classified as a single SELECT.</summary>
    public string? MaterializedObjectName { get; set; }
    /// <summary>The plain name later cells should use in FROM to reference this cell's result.</summary>
    public string? ReferenceToken { get; set; }
}

public class RunAllResult
{
    public List<CellRunSummary> Cells { get; set; } = new();
}

public class CellRunSummary
{
    public string CellId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "success" | "error" | "skipped"
    public string? Error { get; set; }
    /// <summary>The full run result (rows/columns/etc.), when the cell actually ran (null when skipped).</summary>
    public NotebookCellRunResult? Result { get; set; }
}

public class NotebookAiAssistRequest
{
    public string Message { get; set; } = string.Empty;
}

public class NotebookAiAssistResult
{
    public string Reply { get; set; } = string.Empty;
    public string? Sql { get; set; }
}

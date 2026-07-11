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
    /// <summary>Whether the requesting user may rename/delete/manage sharing for this notebook (creator or admin).</summary>
    public bool CanEdit { get; set; }
    /// <summary>Whether the requesting user may add/edit/run cells — true for the owner, company admins,
    /// anyone when the notebook is company-wide shared, or a user with an explicit Editor grant.</summary>
    public bool CanEditCells { get; set; }
    /// <summary>Populated on both the list and single-notebook endpoints (even when Cells itself isn't).</summary>
    public int CellCount { get; set; }
    public List<NotebookCellDto> Cells { get; set; } = new();

    public string? CronExpression { get; set; }
    public bool ScheduleEnabled { get; set; }
    public string? ScheduleTimeZone { get; set; }
    public DateTime? LastScheduledRunAt { get; set; }
    public string? LastScheduledRunStatus { get; set; }
    public string? LastScheduledRunError { get; set; }
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

public class DuplicateNotebookRequest
{
    public string? Name { get; set; }
}

/// <summary>Portable JSON snapshot of a notebook for export/import. Cell <see cref="NotebookExportCellDto.Id"/>
/// is the ORIGINAL cell id, kept only so <see cref="NotebookExportCellDto.ReferencedCellIds"/> can be
/// remapped to freshly-generated ids on import — it's not reused as the new cell's id.</summary>
public class NotebookExportDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<NotebookExportCellDto> Cells { get; set; } = new();
}

public class NotebookExportCellDto
{
    public string Id { get; set; } = string.Empty;
    public string CellType { get; set; } = "sql";
    public string? Name { get; set; }
    public string? Sql { get; set; }
    public string? Markdown { get; set; }
    public List<string> ReferencedCellIds { get; set; } = new();
    public bool SnapshotMode { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Advisory — dataset ids are company-specific. Re-validated on import; dropped (cell comes in
    /// with no dataset) if the importing company can't actually access it.</summary>
    public string? DatasetId { get; set; }
    public string? DatasetName { get; set; }
}

public class ScheduleNotebookRequest
{
    /// <summary>Standard 5-field cron. Ignored (schedule effectively off) when Enabled is false.</summary>
    public string? CronExpression { get; set; }
    public bool Enabled { get; set; }
    /// <summary>IANA id (e.g. "Asia/Beirut"); null uses the scheduler's default.</summary>
    public string? TimeZone { get; set; }
}

/// <summary>Optional run-time inputs for a cell/notebook run — substituted into <c>{{name}}</c> placeholders
/// in SQL text before execution. Never persisted; a fresh run with no matching key leaves that placeholder
/// untouched (so it fails loudly at the SQL level instead of silently becoming an empty string).</summary>
public class RunNotebookRequest
{
    public Dictionary<string, string>? Parameters { get; set; }
}

public class NotebookCellRunDto
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int? RowsReturned { get; set; }
    public long? ElapsedMs { get; set; }
    public string? MaterializedObject { get; set; }
    public string TriggeredBy { get; set; } = "manual";
    public DateTime StartedAt { get; set; }
}

public class NotebookStorageSummaryDto
{
    public int ObjectCount { get; set; }
    public long TotalRows { get; set; }
    public long TotalSizeBytes { get; set; }
}

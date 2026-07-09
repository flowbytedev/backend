using System.Collections.Generic;

namespace Application.Shared.Models.Data;

/// <summary>
/// A single column merged with its saved documentation (if any). Every live column of the table is
/// returned, documented or not — undocumented columns simply have null doc fields.
/// </summary>
public class ColumnDocDto
{
    public string ColumnName { get; set; } = string.Empty;
    /// <summary>Structural type from DuckDB / the source (informational; not editable here).</summary>
    public string DataType { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? SemanticType { get; set; }
    public string? Unit { get; set; }
    public bool IsPii { get; set; }
    public string? PiiType { get; set; }
    /// <summary>True when the current values came from the AI generator and haven't been hand-edited.</summary>
    public bool IsAiGenerated { get; set; }
}

/// <summary>Documentation for all columns of one dataset table.</summary>
public class TableDocDto
{
    public string DatasetId { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public List<ColumnDocDto> Columns { get; set; } = new();
}

/// <summary>A user's edit to one column's documentation (upserted).</summary>
public class SaveColumnDocRequest
{
    public string ColumnName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? SemanticType { get; set; }
    public string? Unit { get; set; }
    public bool IsPii { get; set; }
    public string? PiiType { get; set; }
}

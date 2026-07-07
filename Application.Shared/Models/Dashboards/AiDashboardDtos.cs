using System;
using System.Collections.Generic;

namespace Application.Shared.Models.Dashboards;

/// <summary>Field mappings + formatting for a widget, serialized into <see cref="AiDashboardWidget.ConfigJson"/>.</summary>
public class WidgetConfig
{
    // Column used for the category/x-axis (bar/line) — the first non-numeric column by default.
    public string? XField { get; set; }
    // Optional column that splits data into multiple series.
    public string? SeriesField { get; set; }
    // Column holding the numeric value to plot (bar/line) or show (kpi).
    public string? ValueField { get; set; }
    // A hint the model may set (sum/avg/count/…); descriptive only — the SQL already aggregates.
    public string? Aggregate { get; set; }
    // Optional client-side row cap for display.
    public int? Limit { get; set; }
    // Optional number format hint for kpi (e.g. "N0", "C0").
    public string? NumberFormat { get; set; }
}

/// <summary>Client-facing widget.</summary>
public class AiDashboardWidgetDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string VizType { get; set; } = "table";
    public string Sql { get; set; } = string.Empty;
    public WidgetConfig Config { get; set; } = new();
    public int SortOrder { get; set; }
}

/// <summary>Client-facing dashboard with its ordered widgets.</summary>
public class AiDashboardDto
{
    public string Id { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public List<AiDashboardWidgetDto> Widgets { get; set; } = new();
}

/// <summary>Create-dashboard payload.</summary>
public class CreateAiDashboardRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>Rename/update-dashboard payload.</summary>
public class UpdateAiDashboardRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>Manual widget create/update payload (direct canvas edits).</summary>
public class SaveWidgetRequest
{
    public string Title { get; set; } = string.Empty;
    public string VizType { get; set; } = "table";
    public string Sql { get; set; } = string.Empty;
    public WidgetConfig Config { get; set; } = new();
}

/// <summary>Chat turn against a dashboard.</summary>
public class DashboardChatRequest
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of a chat turn: the assistant's reply plus the updated dashboard spec.</summary>
public class DashboardChatResponse
{
    public string Reply { get; set; } = string.Empty;
    public AiDashboardDto Dashboard { get; set; } = new();
}

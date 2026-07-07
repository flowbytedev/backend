using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Application.Shared.Data;
using Application.Shared.Models.Dashboards;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

public interface IAiDashboardService
{
    Task<List<AiDashboardDto>> GetForDatasetAsync(string companyId, string datasetId);
    Task<AiDashboardDto?> GetAsync(string companyId, string id);
    Task<AiDashboardDto> CreateAsync(string companyId, string datasetId, string userId, CreateAiDashboardRequest request);
    Task<AiDashboardDto?> RenameAsync(string companyId, string id, UpdateAiDashboardRequest request);
    Task<bool> DeleteAsync(string companyId, string id);

    Task<AiDashboardWidgetDto?> AddWidgetAsync(string companyId, string dashboardId, SaveWidgetRequest request);
    Task<AiDashboardWidgetDto?> UpdateWidgetAsync(string companyId, string dashboardId, string widgetId, SaveWidgetRequest request);
    Task<bool> RemoveWidgetAsync(string companyId, string dashboardId, string widgetId);
    Task<bool> ReorderWidgetsAsync(string companyId, string dashboardId, List<string> orderedWidgetIds);
}

/// <summary>
/// CRUD for AI-built dashboards and their widgets. Every operation is scoped by company (and, for
/// widgets, by parent dashboard). Mirrors <see cref="SavedQueryService"/>. No cascade deletes — a
/// dashboard delete removes its widgets explicitly.
/// </summary>
public class AiDashboardService : IAiDashboardService
{
    private readonly ApplicationDbContext _db;

    public AiDashboardService(ApplicationDbContext db) => _db = db;

    public async Task<List<AiDashboardDto>> GetForDatasetAsync(string companyId, string datasetId)
    {
        var dashboards = await _db.AiDashboard
            .Where(d => d.CompanyId == companyId && d.DatasetId == datasetId)
            .OrderByDescending(d => d.ModifiedAt ?? d.CreatedAt)
            .AsNoTracking()
            .ToListAsync();
        if (dashboards.Count == 0) return new();

        var ids = dashboards.Select(d => d.Id).ToList();
        var widgets = await _db.AiDashboardWidget
            .Where(w => w.CompanyId == companyId && ids.Contains(w.DashboardId))
            .AsNoTracking()
            .ToListAsync();

        var byDashboard = widgets.GroupBy(w => w.DashboardId).ToDictionary(g => g.Key, g => g.ToList());
        return dashboards.Select(d => ToDto(d, byDashboard.TryGetValue(d.Id, out var ws) ? ws : new())).ToList();
    }

    public async Task<AiDashboardDto?> GetAsync(string companyId, string id)
    {
        var dashboard = await _db.AiDashboard.AsNoTracking()
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Id == id);
        if (dashboard == null) return null;

        var widgets = await _db.AiDashboardWidget.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.DashboardId == id)
            .ToListAsync();
        return ToDto(dashboard, widgets);
    }

    public async Task<AiDashboardDto> CreateAsync(string companyId, string datasetId, string userId, CreateAiDashboardRequest request)
    {
        var dashboard = new AiDashboard
        {
            Id = Guid.NewGuid().ToString(),
            CompanyId = companyId,
            DatasetId = datasetId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Untitled dashboard" : request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description!.Trim(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = userId,
        };
        _db.AiDashboard.Add(dashboard);
        await _db.SaveChangesAsync();
        return ToDto(dashboard, new());
    }

    public async Task<AiDashboardDto?> RenameAsync(string companyId, string id, UpdateAiDashboardRequest request)
    {
        var dashboard = await _db.AiDashboard.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Id == id);
        if (dashboard == null) return null;

        dashboard.Name = string.IsNullOrWhiteSpace(request.Name) ? dashboard.Name : request.Name.Trim();
        dashboard.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description!.Trim();
        dashboard.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAsync(companyId, id);
    }

    public async Task<bool> DeleteAsync(string companyId, string id)
    {
        var dashboard = await _db.AiDashboard.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Id == id);
        if (dashboard == null) return false;

        // No cascade — remove child widgets explicitly first.
        var widgets = await _db.AiDashboardWidget.Where(w => w.CompanyId == companyId && w.DashboardId == id).ToListAsync();
        _db.AiDashboardWidget.RemoveRange(widgets);
        _db.AiDashboard.Remove(dashboard);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<AiDashboardWidgetDto?> AddWidgetAsync(string companyId, string dashboardId, SaveWidgetRequest request)
    {
        var dashboard = await _db.AiDashboard.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Id == dashboardId);
        if (dashboard == null) return null;

        var maxOrder = await _db.AiDashboardWidget
            .Where(w => w.CompanyId == companyId && w.DashboardId == dashboardId)
            .Select(w => (int?)w.SortOrder).MaxAsync() ?? -1;

        var widget = new AiDashboardWidget
        {
            Id = Guid.NewGuid().ToString(),
            DashboardId = dashboardId,
            CompanyId = companyId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled" : request.Title.Trim(),
            VizType = NormalizeViz(request.VizType),
            SqlText = request.Sql ?? string.Empty,
            ConfigJson = JsonSerializer.Serialize(request.Config ?? new WidgetConfig()),
            SortOrder = maxOrder + 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiDashboardWidget.Add(widget);
        dashboard.ModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ToDto(widget);
    }

    public async Task<AiDashboardWidgetDto?> UpdateWidgetAsync(string companyId, string dashboardId, string widgetId, SaveWidgetRequest request)
    {
        var widget = await _db.AiDashboardWidget
            .FirstOrDefaultAsync(w => w.CompanyId == companyId && w.DashboardId == dashboardId && w.Id == widgetId);
        if (widget == null) return null;

        widget.Title = string.IsNullOrWhiteSpace(request.Title) ? widget.Title : request.Title.Trim();
        widget.VizType = NormalizeViz(request.VizType);
        if (!string.IsNullOrWhiteSpace(request.Sql)) widget.SqlText = request.Sql;
        widget.ConfigJson = JsonSerializer.Serialize(request.Config ?? new WidgetConfig());
        await TouchDashboardAsync(companyId, dashboardId);
        await _db.SaveChangesAsync();
        return ToDto(widget);
    }

    public async Task<bool> RemoveWidgetAsync(string companyId, string dashboardId, string widgetId)
    {
        var widget = await _db.AiDashboardWidget
            .FirstOrDefaultAsync(w => w.CompanyId == companyId && w.DashboardId == dashboardId && w.Id == widgetId);
        if (widget == null) return false;

        _db.AiDashboardWidget.Remove(widget);
        await TouchDashboardAsync(companyId, dashboardId);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReorderWidgetsAsync(string companyId, string dashboardId, List<string> orderedWidgetIds)
    {
        var widgets = await _db.AiDashboardWidget
            .Where(w => w.CompanyId == companyId && w.DashboardId == dashboardId)
            .ToListAsync();
        if (widgets.Count == 0) return false;

        for (var i = 0; i < orderedWidgetIds.Count; i++)
        {
            var w = widgets.FirstOrDefault(x => x.Id == orderedWidgetIds[i]);
            if (w != null) w.SortOrder = i;
        }
        await TouchDashboardAsync(companyId, dashboardId);
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task TouchDashboardAsync(string companyId, string dashboardId)
    {
        var dashboard = await _db.AiDashboard.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Id == dashboardId);
        if (dashboard != null) dashboard.ModifiedAt = DateTime.UtcNow;
    }

    private static string NormalizeViz(string? viz)
    {
        var v = (viz ?? "").Trim().ToLowerInvariant();
        return v is "table" or "bar" or "line" or "area" or "pie" or "doughnut" or "kpi" ? v : "table";
    }

    private static AiDashboardDto ToDto(AiDashboard d, List<AiDashboardWidget> widgets) => new()
    {
        Id = d.Id,
        DatasetId = d.DatasetId,
        Name = d.Name,
        Description = d.Description,
        CreatedAt = d.CreatedAt,
        ModifiedAt = d.ModifiedAt,
        Widgets = widgets.OrderBy(w => w.SortOrder).Select(ToDto).ToList(),
    };

    private static AiDashboardWidgetDto ToDto(AiDashboardWidget w) => new()
    {
        Id = w.Id,
        Title = w.Title,
        VizType = w.VizType,
        Sql = w.SqlText,
        Config = DeserializeConfig(w.ConfigJson),
        SortOrder = w.SortOrder,
    };

    private static WidgetConfig DeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<WidgetConfig>(json) ?? new(); }
        catch { return new(); }
    }
}

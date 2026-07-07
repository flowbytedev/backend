using System.Linq;
using System.Threading.Tasks;
using Application.Shared.Authorization;
using Application.Shared.Models.Dashboards;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// Conversational dashboard builder for a dataset. CRUD for AI-built dashboards + widgets, plus a
/// /chat endpoint that runs the planner agent, validates and applies the resulting operations, and
/// returns the updated dashboard. Widget data is fetched at render time via the existing
/// <c>POST /api/datasets/{datasetId}/query/run</c> endpoint — this controller never executes ad-hoc SQL.
/// </summary>
[Route("api/datasets/{datasetId}/ai-dashboards")]
[ApiController]
[Authorize(Policy = PolicyNames.DatasetsAccess)]
public class AiDashboardsController : ControllerBase
{
    private readonly IAiDashboardService _dashboards;
    private readonly IDashboardAgentService _agent;

    public AiDashboardsController(IAiDashboardService dashboards, IDashboardAgentService agent)
    {
        _dashboards = dashboards;
        _agent = agent;
    }

    // GET: list dashboards for a dataset
    [HttpGet]
    public async Task<ActionResult<List<AiDashboardDto>>> List(string datasetId)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        return Ok(await _dashboards.GetForDatasetAsync(companyId, datasetId));
    }

    // GET: one dashboard
    [HttpGet("{id}")]
    public async Task<ActionResult<AiDashboardDto>> Get(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        var dashboard = await _dashboards.GetAsync(companyId, id);
        return dashboard == null ? NotFound() : Ok(dashboard);
    }

    // POST: create a dashboard
    [HttpPost]
    public async Task<ActionResult<AiDashboardDto>> Create(string datasetId, [FromBody] CreateAiDashboardRequest request)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        var created = await _dashboards.CreateAsync(companyId, datasetId, userId, request ?? new());
        return Ok(created);
    }

    // PUT: rename/update a dashboard
    [HttpPut("{id}")]
    public async Task<ActionResult<AiDashboardDto>> Update(string datasetId, string id, [FromBody] UpdateAiDashboardRequest request)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        var updated = await _dashboards.RenameAsync(companyId, id, request ?? new());
        return updated == null ? NotFound() : Ok(updated);
    }

    // DELETE: delete a dashboard
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string datasetId, string id)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        return await _dashboards.DeleteAsync(companyId, id) ? NoContent() : NotFound();
    }

    // POST: conversational turn — plan, validate, apply, return the updated dashboard.
    [HttpPost("{id}/chat")]
    public async Task<ActionResult<DashboardChatResponse>> Chat(string datasetId, string id, [FromBody] DashboardChatRequest request)
    {
        var (companyId, userId, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        if (string.IsNullOrWhiteSpace(request?.Message)) return BadRequest("Message is required");

        var dashboard = await _dashboards.GetAsync(companyId, id);
        if (dashboard == null) return NotFound();

        var agentRequest = new DashboardAgentRequest
        {
            DatasetId = datasetId,
            CompanyId = companyId,
            UserId = userId,
            Message = request.Message.Trim(),
            ExistingWidgets = dashboard.Widgets.Select(w => new DashboardWidgetContext
            {
                Id = w.Id, Title = w.Title, VizType = w.VizType, Sql = w.Sql,
            }).ToList(),
        };

        var plan = await _agent.PlanAsync(agentRequest, HttpContext.RequestAborted);

        foreach (var op in plan.Operations)
            await ApplyOperationAsync(companyId, id, op, dashboard);

        var updated = await _dashboards.GetAsync(companyId, id) ?? dashboard;
        return Ok(new DashboardChatResponse { Reply = plan.Reply, Dashboard = updated });
    }

    // POST: add a widget directly (manual canvas edit)
    [HttpPost("{id}/widgets")]
    public async Task<ActionResult<AiDashboardWidgetDto>> AddWidget(string datasetId, string id, [FromBody] SaveWidgetRequest request)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");
        if (!SelectOnlyGuard.IsSafeSelect(request.Sql, out var guardError))
            return BadRequest(guardError ?? "A read-only SELECT is required.");
        var widget = await _dashboards.AddWidgetAsync(companyId, id, request);
        return widget == null ? NotFound() : Ok(widget);
    }

    // PUT: update a widget directly
    [HttpPut("{id}/widgets/{widgetId}")]
    public async Task<ActionResult<AiDashboardWidgetDto>> UpdateWidget(string datasetId, string id, string widgetId, [FromBody] SaveWidgetRequest request)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        if (request == null) return BadRequest("Request is required");
        if (!string.IsNullOrWhiteSpace(request.Sql))
        {
            if (!SelectOnlyGuard.IsSafeSelect(request.Sql, out var guardError))
                return BadRequest(guardError ?? "A read-only SELECT is required.");
        }
        var widget = await _dashboards.UpdateWidgetAsync(companyId, id, widgetId, request);
        return widget == null ? NotFound() : Ok(widget);
    }

    // DELETE: remove a widget
    [HttpDelete("{id}/widgets/{widgetId}")]
    public async Task<IActionResult> RemoveWidget(string datasetId, string id, string widgetId)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        return await _dashboards.RemoveWidgetAsync(companyId, id, widgetId) ? NoContent() : NotFound();
    }

    // PUT: reorder widgets. Distinct route (not under {id}/widgets/...) so it can't collide with the
    // {id}/widgets/{widgetId} update route.
    [HttpPut("{id}/reorder")]
    public async Task<IActionResult> ReorderWidgets(string datasetId, string id, [FromBody] List<string> orderedWidgetIds)
    {
        var (companyId, _, error) = ReadHeaders();
        if (error != null) return BadRequest(error);
        if (!User.HasCompanyRole(companyId, "VIEW_DATA")) return Forbid();
        if (orderedWidgetIds == null || orderedWidgetIds.Count == 0) return BadRequest("No widget order provided.");
        return await _dashboards.ReorderWidgetsAsync(companyId, id, orderedWidgetIds) ? NoContent() : NotFound();
    }

    // Applies a single validated planner operation against the dashboard. Add/Update ops carry SQL that
    // the agent already validated; a defensive SelectOnlyGuard check keeps unsafe SQL out regardless.
    private async Task ApplyOperationAsync(string companyId, string dashboardId, DashboardOperation op, AiDashboardDto current)
    {
        switch (op.Type)
        {
            case DashboardOpType.AddWidget:
                if (SelectOnlyGuard.IsSafeSelect(op.Sql, out _))
                    await _dashboards.AddWidgetAsync(companyId, dashboardId, ToSaveRequest(op));
                break;

            case DashboardOpType.UpdateWidget:
                if (!string.IsNullOrWhiteSpace(op.WidgetId) && SelectOnlyGuard.IsSafeSelect(op.Sql, out _))
                    await _dashboards.UpdateWidgetAsync(companyId, dashboardId, op.WidgetId!, ToSaveRequest(op));
                break;

            case DashboardOpType.RemoveWidget:
                if (!string.IsNullOrWhiteSpace(op.WidgetId))
                    await _dashboards.RemoveWidgetAsync(companyId, dashboardId, op.WidgetId!);
                break;

            case DashboardOpType.RenameWidget:
                // Rename keeps the existing SQL/viz/config; only the title changes.
                var existing = current.Widgets.FirstOrDefault(w => w.Id == op.WidgetId);
                if (existing != null && !string.IsNullOrWhiteSpace(op.Title))
                    await _dashboards.UpdateWidgetAsync(companyId, dashboardId, existing.Id, new SaveWidgetRequest
                    {
                        Title = op.Title!, VizType = existing.VizType, Sql = existing.Sql, Config = existing.Config,
                    });
                break;
        }
    }

    private static SaveWidgetRequest ToSaveRequest(DashboardOperation op) => new()
    {
        Title = op.Title ?? "Untitled",
        VizType = op.VizType ?? "table",
        Sql = op.Sql ?? string.Empty,
        Config = op.Config ?? new WidgetConfig(),
    };

    private (string companyId, string userId, string? error) ReadHeaders()
    {
        var companyId = Request.Headers["X-Company-ID"].FirstOrDefault() ?? "";
        var userId = Request.Headers["UserId"].ToString();
        if (string.IsNullOrWhiteSpace(companyId)) return ("", "", "Company ID is required");
        if (string.IsNullOrWhiteSpace(userId)) return ("", "", "User ID is required in headers");
        return (companyId, userId, null);
    }
}

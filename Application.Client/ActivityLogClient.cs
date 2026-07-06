using System.Net.Http.Json;
using Application.Shared.Models.Logging;

namespace Application.Client.Services;

/// <summary>
/// Fire-and-forget client for logging pure client-side UI events (page open, client-only sort/search)
/// that never reach a dataset/table API endpoint. Posts to <c>api/data-log/ui-event</c>. Never throws.
/// </summary>
public class ActivityLogClient
{
    private readonly HttpClient _http;

    public ActivityLogClient(HttpClient http) => _http = http;

    public async Task LogAsync(
        string? companyId,
        string action,
        string? area = null,
        string? datasetId = null,
        string? tableName = null,
        string? route = null,
        string? details = null)
    {
        if (string.IsNullOrWhiteSpace(companyId) || string.IsNullOrWhiteSpace(action))
            return;

        try
        {
            // Use a per-call request so we don't mutate shared DefaultRequestHeaders across pages.
            var request = new HttpRequestMessage(HttpMethod.Post, "api/data-log/ui-event");
            request.Headers.Add("X-Company-ID", companyId);
            request.Content = JsonContent.Create(new UiActivityLogRequest
            {
                Action = action,
                Area = area,
                DatasetId = datasetId,
                TableName = tableName,
                Route = route,
                Details = details
            });
            await _http.SendAsync(request);
        }
        catch
        {
            // UI-event logging must never disrupt the page.
        }
    }
}

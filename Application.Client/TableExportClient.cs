using System.Net.Http.Json;
using Application.Shared.Models.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Application.Client.Services;

/// <summary>
/// Downloads a dataset table as CSV: a POST that prepares the file server-side, then a browser navigation
/// that collects it.
/// </summary>
/// <remarks>
/// <para>
/// The file is never read into WebAssembly memory. The prepare call returns a one-time link and the
/// browser's own download manager fetches it, so what a user can export is bounded by their disk rather
/// than by the WASM heap — which is 32-bit and nowhere near large enough for a multi-million-row CSV.
/// </para>
/// <para>
/// This owns a private <see cref="HttpClient"/> instead of taking the injected one for a single reason:
/// the shared client keeps <see cref="HttpClient"/>'s default <b>100-second</b> timeout, and preparing a
/// very large export legitimately runs longer. The timeout is per-client, and a per-request
/// <see cref="CancellationToken"/> can only shorten it — so a second, longer-lived client is the only way
/// to allow it here without relaxing it for every call in the app.
/// </para>
/// </remarks>
public class TableExportClient : IDisposable
{
    /// <summary>Generous, but not unbounded — a wedged request should still surface as an error eventually.</summary>
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(20);

    private readonly HttpClient _http;
    private readonly IJSRuntime _js;

    public TableExportClient(NavigationManager navigation, IJSRuntime js)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(navigation.BaseUri),
            Timeout = PrepareTimeout,
        };
        _js = js;
    }

    /// <summary>
    /// Prepares the export and hands the link to the browser. Returns the ticket so the caller can report
    /// what was written; throws with the server's own message when the export fails.
    /// </summary>
    /// <param name="query">
    /// Filters, sort and column selection to apply. Null exports the whole table.
    /// </param>
    public async Task<TableExportTicketResponse> DownloadAsync(
        string datasetId,
        string tableName,
        string companyId,
        string userId,
        TableDataQuery? query = null,
        CancellationToken ct = default)
    {
        // Per-call request with explicit headers, so this never mutates DefaultRequestHeaders shared with
        // the pages — same reason ActivityLogClient does it this way.
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/Datasets/{datasetId}/tables/{tableName}/export");
        request.Headers.Add("X-Company-ID", companyId);
        request.Headers.Add("UserId", userId);
        request.Content = JsonContent.Create(query ?? new TableDataQuery());

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await DescribeFailure(response));

        var ticket = await response.Content.ReadFromJsonAsync<TableExportTicketResponse>(ct)
                     ?? throw new InvalidOperationException("The server did not return a download link.");

        // A same-origin anchor click: the session cookie goes with it, which is how the collection
        // endpoint authenticates a request that cannot carry our headers.
        await _js.InvokeVoidAsync("downloadFile", ct, ticket.Url);
        return ticket;
    }

    private static async Task<string> DescribeFailure(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body)
            ? $"Export failed: {response.StatusCode}"
            : body.Trim().Trim('"');
    }

    public void Dispose() => _http.Dispose();
}

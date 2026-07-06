namespace Application.Shared.Models.Logging;

/// <summary>
/// Payload for a pure client-side UI event (page open, client-only sort/search/paging) that never
/// reaches a dataset/table API endpoint. Posted to <c>api/data-log/ui-event</c>.
/// </summary>
public class UiActivityLogRequest
{
    /// <summary>Action identifier, e.g. <c>table.page_open</c>, <c>table.sort</c>, <c>dataset.search</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary><c>dataset</c> | <c>table</c> | <c>ingestion</c> | <c>sharing</c>. Optional; derived from Action if omitted.</summary>
    public string? Area { get; set; }

    public string? DatasetId { get; set; }
    public string? TableName { get; set; }
    public string? Route { get; set; }

    /// <summary>Free-form JSON/string context (e.g. the sort column, search term).</summary>
    public string? Details { get; set; }
}

namespace Application.Shared.Models.Logging;

/// <summary>Filters + paging for reading the <c>data_app_log</c> audit log.</summary>
public class DataAppLogQuery
{
    public string CompanyId { get; set; } = string.Empty;

    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public string? Source { get; set; }     // 'api' | 'client'
    public string? Area { get; set; }       // dataset | table | ingestion | sharing
    public string? Action { get; set; }
    public string? UserId { get; set; }
    public string? DatasetId { get; set; }
    public string? TableName { get; set; }

    /// <summary>Substring match across user, action, table, query text and details.</summary>
    public string? Search { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

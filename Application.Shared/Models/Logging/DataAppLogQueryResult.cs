namespace Application.Shared.Models.Logging;

/// <summary>A single audit-log row returned to the viewer.</summary>
public class DataAppLogRecord
{
    public DateTime EventTime { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
    public long RowCount { get; set; }
    public int StatusCode { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>A page of audit-log rows plus the total matching count.</summary>
public class DataAppLogQueryResult
{
    public List<DataAppLogRecord> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

namespace Application.Shared.Models.Logging;

/// <summary>
/// One audit/usage log row for the Datasets &amp; Tables feature. Mirrors the columns of the
/// ClickHouse <c>data_app_log.data_app_log</c> table.
/// </summary>
public class DataAppLogEntry
{
    /// <summary>When the action happened (UTC).</summary>
    public DateTime EventTime { get; set; } = DateTime.UtcNow;

    public string CompanyId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>Origin of the event: <c>api</c> (server action filter) or <c>client</c> (UI event).</summary>
    public string Source { get; set; } = "api";

    /// <summary>Functional area: <c>dataset</c> | <c>table</c> | <c>ingestion</c> | <c>sharing</c>.</summary>
    public string Area { get; set; } = string.Empty;

    /// <summary>Action identifier, e.g. <c>table.open</c>, <c>table.query</c>, <c>row.add</c>, <c>table.download</c>.</summary>
    public string Action { get; set; } = string.Empty;

    public string DatasetId { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;

    public string HttpMethod { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string QueryString { get; set; } = string.Empty;

    /// <summary>Executed/requested SQL, or a serialized description of the filters/sort applied.</summary>
    public string QueryText { get; set; } = string.Empty;

    public long RowCount { get; set; }
    public int StatusCode { get; set; }
    public bool Success { get; set; } = true;
    public string Error { get; set; } = string.Empty;
    public long DurationMs { get; set; }

    public string ClientIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>JSON blob with request arguments / extra context.</summary>
    public string Details { get; set; } = string.Empty;
}

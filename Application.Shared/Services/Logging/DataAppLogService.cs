using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Application.Shared.Models.Logging;
using ClickHouse.Client.ADO;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services.Logging;

/// <summary>
/// Channel-buffered writer for the Datasets &amp; Tables audit/usage log. Requests call
/// <see cref="Enqueue"/> (non-blocking); a single background worker drains the buffer and inserts
/// batches into ClickHouse. All ClickHouse failures are swallowed and logged — audit logging must
/// never break or slow a user request.
/// </summary>
public class DataAppLogService : IDataAppLogService
{
    private readonly DataAppLogSettings _settings;
    private readonly ILogger<DataAppLogService> _logger;
    private readonly Channel<DataAppLogEntry> _channel;

    public DataAppLogService(DataAppLogSettings settings, ILogger<DataAppLogService> logger)
    {
        _settings = settings;
        _logger = logger;
        _channel = Channel.CreateBounded<DataAppLogEntry>(new BoundedChannelOptions(Math.Max(1, settings.QueueCapacity))
        {
            // Drop the oldest queued entry rather than block the request thread when the buffer is full.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool Enabled => _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.ConnectionString);

    /// <summary>Kept in sync with Application.Database.Log/data_app_log.sql.</summary>
    public static string SchemaDdl(string table)
    {
        var db = table.Contains('.') ? table[..table.IndexOf('.')] : "data_app_log";
        return $@"
CREATE DATABASE IF NOT EXISTS {db};

CREATE TABLE IF NOT EXISTS {table}
(
    event_id      UUID          DEFAULT generateUUIDv4(),
    event_time    DateTime64(3) DEFAULT now64(3),
    company_id    LowCardinality(String),
    user_id       String,
    user_name     String,
    source        LowCardinality(String),
    area          LowCardinality(String),
    action        LowCardinality(String),
    dataset_id    String,
    table_name    String,
    http_method   LowCardinality(String),
    route         String,
    query_string  String,
    query_text    String,
    row_count     Int64  DEFAULT 0,
    status_code   Int32  DEFAULT 0,
    success       UInt8  DEFAULT 1,
    error         String,
    duration_ms   Int64  DEFAULT 0,
    client_ip     String,
    user_agent    String,
    details       String,
    level         LowCardinality(String),
    category      LowCardinality(String),
    message       String
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(event_time)
ORDER BY (company_id, event_time, action)
TTL toDateTime(event_time) + INTERVAL 180 DAY;

ALTER TABLE {table} ADD COLUMN IF NOT EXISTS level LowCardinality(String);
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS category LowCardinality(String);
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS message String;";
    }

    public void Enqueue(DataAppLogEntry entry)
    {
        if (!Enabled || entry is null) return;
        // Bounded + DropOldest => TryWrite always succeeds without blocking.
        _channel.Writer.TryWrite(entry);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled) return;
        try
        {
            using var connection = new ClickHouseConnection(_settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var stmt in SplitStatements(SchemaDdl(_settings.Table)))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            _logger.LogInformation("data_app_log schema ensured on ClickHouse ({Table}).", _settings.Table);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure data_app_log schema; audit logging may fail to insert.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;

        var reader = _channel.Reader;
        var batch = new List<DataAppLogEntry>(_settings.BatchSize);

        while (!cancellationToken.IsCancellationRequested)
        {
            batch.Clear();
            try
            {
                if (!await reader.WaitToReadAsync(cancellationToken))
                    break;

                while (batch.Count < _settings.BatchSize && reader.TryRead(out var entry))
                    batch.Add(entry);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (batch.Count > 0)
                await FlushAsync(batch, cancellationToken);
        }

        // Best-effort final drain on shutdown.
        batch.Clear();
        while (reader.TryRead(out var entry))
            batch.Add(entry);
        if (batch.Count > 0)
            await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<DataAppLogEntry> batch, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new ClickHouseConnection(_settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = BuildInsert(batch);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Never rethrow — dropping a batch of audit rows must not affect the app.
            _logger.LogWarning(ex, "Failed to write {Count} data_app_log rows to ClickHouse.", batch.Count);
        }
    }

    private string BuildInsert(List<DataAppLogEntry> batch)
    {
        var sb = new StringBuilder();
        sb.Append("INSERT INTO ").Append(_settings.Table).Append(' ')
          .Append("(event_time, company_id, user_id, user_name, source, area, action, dataset_id, table_name, ")
          .Append("http_method, route, query_string, query_text, row_count, status_code, success, error, ")
          .Append("duration_ms, client_ip, user_agent, details, level, category, message) VALUES ");

        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = batch[i];
            sb.Append('(')
              .Append(Dt(e.EventTime)).Append(',')
              .Append(S(e.CompanyId)).Append(',')
              .Append(S(e.UserId)).Append(',')
              .Append(S(e.UserName)).Append(',')
              .Append(S(e.Source)).Append(',')
              .Append(S(e.Area)).Append(',')
              .Append(S(e.Action)).Append(',')
              .Append(S(e.DatasetId)).Append(',')
              .Append(S(e.TableName)).Append(',')
              .Append(S(e.HttpMethod)).Append(',')
              .Append(S(e.Route)).Append(',')
              .Append(S(e.QueryString)).Append(',')
              .Append(S(e.QueryText)).Append(',')
              .Append(e.RowCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Success ? "1" : "0").Append(',')
              .Append(S(e.Error)).Append(',')
              .Append(e.DurationMs.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(S(e.ClientIp)).Append(',')
              .Append(S(e.UserAgent)).Append(',')
              .Append(S(e.Details)).Append(',')
              .Append(S(e.Level)).Append(',')
              .Append(S(e.Category)).Append(',')
              .Append(S(e.Message))
              .Append(')');
        }

        return sb.ToString();
    }

    private const string ReadColumns =
        "event_time, company_id, user_id, user_name, source, area, action, dataset_id, table_name, " +
        "http_method, route, query_text, row_count, status_code, success, error, duration_ms, client_ip, details, " +
        "level, category, message";

    public async Task<DataAppLogQueryResult> QueryAsync(DataAppLogQuery query, CancellationToken cancellationToken = default)
    {
        var result = new DataAppLogQueryResult
        {
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 500)
        };

        // Never return cross-company rows; a blank company yields an empty result rather than everything.
        if (!Enabled || string.IsNullOrWhiteSpace(query.CompanyId))
            return result;

        var where = BuildWhere(query);
        var offset = (result.Page - 1) * result.PageSize;

        try
        {
            using var connection = new ClickHouseConnection(_settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = $"SELECT count() FROM {_settings.Table} {where}";
                var scalar = await countCmd.ExecuteScalarAsync(cancellationToken);
                result.Total = Convert.ToInt32(scalar);
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {ReadColumns} FROM {_settings.Table} {where} " +
                $"ORDER BY event_time DESC LIMIT {result.PageSize} OFFSET {offset}";

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Items.Add(new DataAppLogRecord
                {
                    EventTime = ToDt(reader.GetValue(0)),
                    CompanyId = ToStr(reader.GetValue(1)),
                    UserId = ToStr(reader.GetValue(2)),
                    UserName = ToStr(reader.GetValue(3)),
                    Source = ToStr(reader.GetValue(4)),
                    Area = ToStr(reader.GetValue(5)),
                    Action = ToStr(reader.GetValue(6)),
                    DatasetId = ToStr(reader.GetValue(7)),
                    TableName = ToStr(reader.GetValue(8)),
                    HttpMethod = ToStr(reader.GetValue(9)),
                    Route = ToStr(reader.GetValue(10)),
                    QueryText = ToStr(reader.GetValue(11)),
                    RowCount = ToLong(reader.GetValue(12)),
                    StatusCode = (int)ToLong(reader.GetValue(13)),
                    Success = ToLong(reader.GetValue(14)) != 0,
                    Error = ToStr(reader.GetValue(15)),
                    DurationMs = ToLong(reader.GetValue(16)),
                    ClientIp = ToStr(reader.GetValue(17)),
                    Details = ToStr(reader.GetValue(18)),
                    Level = ToStr(reader.GetValue(19)),
                    Category = ToStr(reader.GetValue(20)),
                    Message = ToStr(reader.GetValue(21)),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read data_app_log for company {CompanyId}.", query.CompanyId);
        }

        return result;
    }

    private static string BuildWhere(DataAppLogQuery q)
    {
        var clauses = new List<string> { $"company_id = '{Esc(q.CompanyId)}'" };

        if (q.From.HasValue) clauses.Add($"event_time >= {Dt(q.From.Value)}");
        if (q.To.HasValue) clauses.Add($"event_time <= {Dt(q.To.Value)}");
        if (!string.IsNullOrWhiteSpace(q.Source)) clauses.Add($"source = '{Esc(q.Source)}'");
        if (!string.IsNullOrWhiteSpace(q.Area)) clauses.Add($"area = '{Esc(q.Area)}'");
        if (!string.IsNullOrWhiteSpace(q.Action)) clauses.Add($"action = '{Esc(q.Action)}'");
        if (!string.IsNullOrWhiteSpace(q.UserId)) clauses.Add($"user_id = '{Esc(q.UserId)}'");
        if (!string.IsNullOrWhiteSpace(q.DatasetId)) clauses.Add($"dataset_id = '{Esc(q.DatasetId)}'");
        if (!string.IsNullOrWhiteSpace(q.TableName)) clauses.Add($"table_name = '{Esc(q.TableName)}'");
        if (!string.IsNullOrWhiteSpace(q.Level)) clauses.Add($"level = '{Esc(q.Level)}'");
        if (!string.IsNullOrWhiteSpace(q.Category)) clauses.Add($"category = '{Esc(q.Category)}'");

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = Esc(q.Search);
            clauses.Add(
                $"(user_name ILIKE '%{s}%' OR user_id ILIKE '%{s}%' OR action ILIKE '%{s}%' " +
                $"OR table_name ILIKE '%{s}%' OR query_text ILIKE '%{s}%' OR details ILIKE '%{s}%' " +
                $"OR message ILIKE '%{s}%')");
        }

        return "WHERE " + string.Join(" AND ", clauses);
    }

    private static string Esc(string? value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");

    private static string ToStr(object? v) => v is null or DBNull ? string.Empty : v.ToString() ?? string.Empty;

    private static long ToLong(object? v) => v switch
    {
        null or DBNull => 0,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        ulong ul => (long)ul,
        _ => long.TryParse(v.ToString(), out var r) ? r : 0
    };

    private static DateTime ToDt(object? v) => v switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        null or DBNull => default,
        _ => DateTime.TryParse(v.ToString(), out var r) ? r : default
    };

    // ---- value formatting (inline, escaped) ----

    private static string S(string? value)
    {
        var v = value ?? string.Empty;
        return "'" + v.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    private static string Dt(DateTime value)
        => $"toDateTime64('{value.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff}', 3)";

    private static IEnumerable<string> SplitStatements(string ddl)
        => ddl.Split(';', StringSplitOptions.RemoveEmptyEntries)
              .Select(s => s.Trim())
              .Where(s => s.Length > 0);
}

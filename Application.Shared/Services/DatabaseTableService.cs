using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

namespace Application.Shared.Services;

public class DatabaseTableService : IDatabaseTableService
{
    private readonly StatusDbContext _context;
    private readonly ICredentialProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DatabaseTableService> _logger;

    // Hard cap on rows the ad-hoc workbench will pull back from an external source in one query.
    private const int MaxExternalRows = 5000;

    public DatabaseTableService(
        StatusDbContext context,
        ICredentialProtector protector,
        IHttpClientFactory httpClientFactory,
        ILogger<DatabaseTableService> logger)
    {
        _context = context;
        _protector = protector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ---- Connection CRUD ----

    public async Task<DatabaseConnectionDto?> GetConnectionAsync(string entityId, string companyId, CancellationToken ct = default)
    {
        var connection = await _context.DatabaseConnections
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);
        return connection == null ? null : ToDto(connection);
    }

    public async Task<DatabaseConnectionDto> SaveConnectionAsync(string entityId, string companyId, DatabaseConnectionRequest request, string? modifiedBy, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var connection = await _context.DatabaseConnections
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);

        var port = request.Port > 0 ? request.Port : DefaultPort(request.DatabaseType);

        if (connection == null)
        {
            connection = new DatabaseConnection
            {
                Id = Guid.NewGuid().ToString(),
                EntityId = entityId,
                CompanyId = companyId,
                DatabaseType = request.DatabaseType,
                Host = request.Host,
                Port = port,
                DatabaseName = request.DatabaseName,
                Username = request.Username,
                SecretEncrypted = string.IsNullOrEmpty(request.Secret) ? null : _protector.Encrypt(request.Secret),
                UseSsl = request.UseSsl,
                FilePath = request.FilePath,
                CreatedBy = modifiedBy,
                CreatedOn = now,
                ModifiedOn = now
            };
            _context.DatabaseConnections.Add(connection);
        }
        else
        {
            connection.DatabaseType = request.DatabaseType;
            connection.Host = request.Host;
            connection.Port = port;
            connection.DatabaseName = request.DatabaseName;
            connection.Username = request.Username;
            connection.UseSsl = request.UseSsl;
            connection.FilePath = request.FilePath;
            connection.ModifiedBy = modifiedBy;
            connection.ModifiedOn = now;

            // Only replace the secret when a new one is supplied; blank keeps the existing one.
            if (!string.IsNullOrEmpty(request.Secret))
                connection.SecretEncrypted = _protector.Encrypt(request.Secret);
        }

        await _context.SaveChangesAsync(ct);
        return ToDto(connection);
    }

    public async Task<bool> DeleteConnectionAsync(string entityId, string companyId, CancellationToken ct = default)
    {
        var connection = await _context.DatabaseConnections
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);
        if (connection == null) return false;

        _context.DatabaseConnections.Remove(connection);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    // ---- Test + discovery ----

    public async Task<DatabaseConnectionTestResult> TestConnectionAsync(string entityId, string companyId, CancellationToken ct = default)
    {
        var connection = await LoadDecryptedAsync(entityId, companyId, ct);
        if (connection == null)
            return new DatabaseConnectionTestResult { Ok = false, Error = "No connection is configured for this entity." };

        try
        {
            await ListTablesAsync(connection, ct);
            return new DatabaseConnectionTestResult { Ok = true };
        }
        catch (Exception ex)
        {
            return new DatabaseConnectionTestResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<DatabaseTableDiscoveryDto> DiscoverTablesAsync(string entityId, string companyId, CancellationToken ct = default)
    {
        var connection = await LoadDecryptedAsync(entityId, companyId, ct);
        if (connection == null)
            throw new InvalidOperationException("No connection is configured for this entity. Configure and save it first.");

        var dto = new DatabaseTableDiscoveryDto();
        List<(string Schema, string Name)> tables;
        try
        {
            tables = await ListTablesAsync(connection, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database table discovery failed for entity {EntityId}", entityId);
            dto.Error = ex.Message;
            return dto;
        }

        dto.Tables = tables
            .Select(t => new DatabaseTableInfoDto
            {
                Schema = t.Schema,
                Name = t.Name,
                FullName = string.IsNullOrEmpty(t.Schema) ? t.Name : $"{t.Schema}.{t.Name}"
            })
            .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Match each discovered table to an existing Table entity (same company, name OrdinalIgnoreCase).
        var existing = await _context.MonitoredAssets
            .Where(e => e.CompanyId == companyId && !e.IsDeleted && e.EntityType == AssetType.Table)
            .Select(e => new { e.Id, e.Name })
            .ToListAsync(ct);

        foreach (var t in dto.Tables)
        {
            var match = existing.FirstOrDefault(e => string.Equals(e.Name, t.FullName, StringComparison.OrdinalIgnoreCase));
            t.ExistingEntityId = match?.Id;
        }

        return dto;
    }

    // ---- Dataset source: candidate databases + ad-hoc query ----

    public async Task<List<DatabaseEntityOptionDto>> GetConnectedDatabasesAsync(string companyId, CancellationToken ct = default)
    {
        // Database entities that actually have a saved connection — only those can back a dataset.
        var query =
            from e in _context.MonitoredAssets
            where e.CompanyId == companyId && !e.IsDeleted && e.EntityType == AssetType.Database
            join c in _context.DatabaseConnections on e.Id equals c.EntityId
            where c.CompanyId == companyId
            orderby e.Name
            select new DatabaseEntityOptionDto { Id = e.Id, Name = e.Name ?? string.Empty, DatabaseType = c.DatabaseType };

        return await query.ToListAsync(ct);
    }

    public async Task<SqlQueryResult> ExecuteQueryAsync(string entityId, string companyId, string sql, int maxRows, CancellationToken ct = default)
    {
        var result = new SqlQueryResult { IsSelect = true };

        if (string.IsNullOrWhiteSpace(sql))
        {
            result.Error = "Query is required.";
            return result;
        }
        // External sources are strictly read-only — reject writes/DDL and stacked statements.
        if (!IsReadOnlyStatement(sql))
        {
            result.Error = "Only a single read-only SELECT statement is allowed against an external database source.";
            return result;
        }

        var connection = await LoadDecryptedAsync(entityId, companyId, ct);
        if (connection == null)
        {
            result.Error = "No connection is configured for this database source.";
            return result;
        }

        var cap = maxRows > 0 ? Math.Min(maxRows, MaxExternalRows) : MaxExternalRows;
        await RunGridAsync(connection, sql, cap, result, ct);
        return result;
    }

    public async Task<SqlQueryResult> GetTableSampleAsync(string entityId, string companyId, string tableName, int maxRows, CancellationToken ct = default)
    {
        var result = new SqlQueryResult { IsSelect = true };

        if (string.IsNullOrWhiteSpace(tableName))
        {
            result.Error = "Table name is required.";
            return result;
        }

        var connection = await LoadDecryptedAsync(entityId, companyId, ct);
        if (connection == null)
        {
            result.Error = "No connection is configured for this database source.";
            return result;
        }

        var cap = maxRows > 0 ? Math.Min(maxRows, MaxExternalRows) : MaxExternalRows;
        // Push the row limit INTO the SQL (server-side) so a heavy view/table isn't fully evaluated just to
        // hand back a few rows — the ADO reader's client-side cap alone can't prevent that.
        var sql = BuildSampleSql(connection.DatabaseType, tableName, cap);
        await RunGridAsync(connection, sql, cap, result, ct);
        return result;
    }

    // Dialect-correct "first N rows" query. SQL Server has no LIMIT (uses TOP); the others take LIMIT.
    private static string BuildSampleSql(DataSourceType type, string tableName, int limit) => type switch
    {
        DataSourceType.SQLServer => $"SELECT TOP {limit} * FROM {tableName}",
        _ => $"SELECT * FROM {tableName} LIMIT {limit}",
    };

    private async Task RunGridAsync(DatabaseConnection connection, string sql, int cap, SqlQueryResult result, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (connection.DatabaseType == DataSourceType.ClickHouse)
                await ExecuteClickHouseGridAsync(connection, sql, cap, result, ct);
            else
                await ExecuteAdoGridAsync(connection, sql, cap, result, ct);
        }
        catch (Exception ex)
        {
            result.Error = Truncate(ex.Message);
        }

        sw.Stop();
        result.ElapsedMs = (long)sw.Elapsed.TotalMilliseconds;
        result.RowsReturned = result.Rows.Count;
    }

    public async Task<SqlQueryResult> GetTableSchemaAsync(string entityId, string companyId, string tableName, CancellationToken ct = default)
    {
        var result = new SqlQueryResult { IsSelect = true };

        if (string.IsNullOrWhiteSpace(tableName))
        {
            result.Error = "Table name is required.";
            return result;
        }

        var connection = await LoadDecryptedAsync(entityId, companyId, ct);
        if (connection == null)
        {
            result.Error = "No connection is configured for this database source.";
            return result;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            if (connection.DatabaseType == DataSourceType.ClickHouse)
                await ReadClickHouseSchemaAsync(connection, tableName, result, ct);
            else
                await ReadAdoSchemaAsync(connection, tableName, result, ct);
        }
        catch (Exception ex)
        {
            result.Error = Truncate(ex.Message);
        }

        sw.Stop();
        result.ElapsedMs = (long)sw.Elapsed.TotalMilliseconds;
        return result;
    }

    // Reads column metadata with CommandBehavior.SchemaOnly: the provider returns the result-set shape
    // (SQL Server via sp_describe_first_result_set, etc.) WITHOUT running the query or fetching any rows.
    // This is what keeps schema discovery cheap even when the "table" is a heavy view.
    private async Task ReadAdoSchemaAsync(DatabaseConnection c, string tableName, SqlQueryResult result, CancellationToken ct)
    {
        await using var connection = CreateAdoConnection(c);
        await connection.OpenAsync(ct);
        await ApplyReadOnlySetupAsync(connection, c, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {tableName}";
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, ct);

        for (int i = 0; i < reader.FieldCount; i++)
            result.Columns.Add(new Column { Name = reader.GetName(i), DataType = SafeTypeName(reader, i) });
    }

    // ClickHouse has no ADO reader; DESCRIBE returns one row per column (name, type, ...) — cheap metadata.
    private async Task ReadClickHouseSchemaAsync(DatabaseConnection c, string tableName, SqlQueryResult result, CancellationToken ct)
    {
        var table = QuoteQualified(DataSourceType.ClickHouse, tableName);
        var body = await QueryClickHouseAsync(c, $"DESCRIBE TABLE {table} FORMAT JSON", ct);

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var col in data.EnumerateArray())
                result.Columns.Add(new Column
                {
                    Name = col.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    DataType = col.TryGetProperty("type", out var t) ? t.GetString() ?? "VARCHAR" : "VARCHAR"
                });
        }
    }

    /// <summary>Allows only a single read-only statement (SELECT/WITH/SHOW/DESCRIBE/EXPLAIN/PRAGMA).
    /// Stacked statements (an inner ';') are rejected so a SELECT can't smuggle a write.</summary>
    private static bool IsReadOnlyStatement(string sql)
    {
        var trimmed = sql.Trim();
        if (trimmed.EndsWith(";")) trimmed = trimmed[..^1].TrimEnd();
        if (trimmed.Contains(';')) return false;

        var firstWord = new string(trimmed.TakeWhile(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        return firstWord is "SELECT" or "WITH" or "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN" or "PRAGMA";
    }

    private async Task ExecuteAdoGridAsync(DatabaseConnection c, string sql, int cap, SqlQueryResult result, CancellationToken ct)
    {
        await using var connection = CreateAdoConnection(c);
        await connection.OpenAsync(ct);

        var readOnlySetup = ReadOnlySetupFor(c.DatabaseType);
        if (!string.IsNullOrEmpty(readOnlySetup))
        {
            await using var setup = connection.CreateCommand();
            setup.CommandText = readOnlySetup;
            await setup.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);

        var fieldCount = reader.FieldCount;
        for (int i = 0; i < fieldCount; i++)
            result.Columns.Add(new Column { Name = reader.GetName(i), DataType = SafeTypeName(reader, i) });

        while (await reader.ReadAsync(ct))
        {
            // Read one row past the cap so we can flag truncation accurately.
            if (result.Rows.Count >= cap) { result.Truncated = true; break; }
            var row = new Dictionary<string, object?>(fieldCount);
            for (int i = 0; i < fieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Rows.Add(row);
        }
    }

    private static string SafeTypeName(System.Data.Common.DbDataReader reader, int i)
    {
        try { return reader.GetDataTypeName(i); }
        catch { return "VARCHAR"; }
    }

    private async Task ExecuteClickHouseGridAsync(DatabaseConnection c, string sql, int cap, SqlQueryResult result, CancellationToken ct)
    {
        // FORMAT JSON returns {meta:[{name,type}], data:[{col:val}]}. Cap rows server-side and stop cleanly.
        var query = $"{sql.TrimEnd().TrimEnd(';')} FORMAT JSON";
        var protocol = c.UseSsl ? "https" : "http";
        var url = $"{protocol}://{c.Host}:{c.Port}/?readonly=1&max_result_rows={cap + 1}&result_overflow_mode=break";

        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(c.Username))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.SecretEncrypted}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }
        request.Content = new StringContent(query, Encoding.UTF8, "text/plain");

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ClickHouse query failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Array)
        {
            foreach (var col in meta.EnumerateArray())
                result.Columns.Add(new Column
                {
                    Name = col.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    DataType = col.TryGetProperty("type", out var t) ? t.GetString() ?? "VARCHAR" : "VARCHAR"
                });
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var rowEl in data.EnumerateArray())
            {
                if (result.Rows.Count >= cap) { result.Truncated = true; break; }
                var row = new Dictionary<string, object?>();
                foreach (var prop in rowEl.EnumerateObject())
                    row[prop.Name] = ConvertJsonElement(prop.Value);
                result.Rows.Add(row);
            }
        }
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
        _ => element.ToString()
    };

    // ---- Commit (mirrors PowerBiService.CommitLineageAsync) ----

    public async Task<DatabaseTableCommitResult> CommitTablesAsync(string entityId, string companyId, DatabaseTableCommitRequest request, string? modifiedBy, CancellationToken ct = default)
    {
        var dbEntity = await _context.MonitoredAssets
            .FirstOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId && !e.IsDeleted, ct);
        if (dbEntity == null)
            throw new InvalidOperationException("Database entity not found.");

        var result = new DatabaseTableCommitResult();
        var now = DateTime.UtcNow;

        var existing = await _context.MonitoredAssets
            .Where(e => e.CompanyId == companyId && !e.IsDeleted && e.EntityType == AssetType.Table)
            .ToListAsync(ct);

        MonitoredAsset? Find(string name) =>
            existing.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        var relevantIds = existing.Select(e => e.Id).Append(entityId).ToHashSet();
        var existingDeps = await _context.AssetDependencies
            .Where(d => d.CompanyId == companyId && relevantIds.Contains(d.EntityId))
            .Select(d => new { d.EntityId, d.DependsOnEntityId })
            .ToListAsync(ct);
        var depPairs = existingDeps.Select(d => (d.EntityId, d.DependsOnEntityId)).ToHashSet();

        void AddDependency(string fromId, string toId, AssetType type, string description)
        {
            if (fromId == toId) return;
            if (!depPairs.Add((fromId, toId))) return;

            _context.AssetDependencies.Add(new AssetDependency
            {
                Id = Guid.NewGuid().ToString(),
                EntityId = fromId,
                DependsOnEntityId = toId,
                DependencyType = type,
                Description = description,
                IsActive = true,
                CompanyId = companyId,
                CreatedBy = modifiedBy,
                CreatedOn = now,
                ModifiedOn = now
            });
            result.DependenciesCreated++;
        }

        foreach (var fullName in request.Tables
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Select(t => t.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var table = Find(fullName);
            if (table == null)
            {
                table = new MonitoredAsset
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = fullName,
                    EntityType = AssetType.Table,
                    Description = "Discovered from database.",
                    IsActive = true,
                    CompanyId = companyId,
                    CreatedBy = modifiedBy,
                    CreatedOn = now,
                    ModifiedOn = now
                };
                _context.MonitoredAssets.Add(table);
                existing.Add(table);
                result.TablesAdded++;
            }
            else
            {
                table.ModifiedBy = modifiedBy;
                table.ModifiedOn = now;
                result.TablesUpdated++;
            }

            // The table depends on this database entity.
            AddDependency(table.Id, entityId, AssetType.Database, "Table in database");
        }

        await _context.SaveChangesAsync(ct);

        result.Message =
            $"{result.TablesAdded} table(s) added, {result.TablesUpdated} updated; " +
            $"{result.DependenciesCreated} dependency link(s) created.";
        return result;
    }

    // ---- Table freshness checks ----

    public async Task<TableCheckDto?> GetTableCheckAsync(string entityId, string companyId, CancellationToken ct = default)
    {
        var check = await _context.TableChecks.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);
        var hasConn = await HasParentConnectionAsync(entityId, companyId, ct);

        // Always return a DTO (defaults when unconfigured) so the UI can show the form and the
        // "needs a database connection" hint before anything is saved.
        return new TableCheckDto
        {
            EntityId = entityId,
            FreshnessColumn = check?.FreshnessColumn,
            MaxAgeMinutes = check?.MaxAgeMinutes ?? 1440,
            IsEnabled = check?.IsEnabled ?? false,
            HasDatabaseConnection = hasConn
        };
    }

    public async Task<TableCheckDto> SaveTableCheckAsync(string entityId, string companyId, TableCheckRequest request, string? modifiedBy, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var check = await _context.TableChecks
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);

        if (check == null)
        {
            check = new TableCheck
            {
                Id = Guid.NewGuid().ToString(),
                EntityId = entityId,
                CompanyId = companyId,
                CreatedBy = modifiedBy,
                CreatedOn = now
            };
            _context.TableChecks.Add(check);
        }

        check.FreshnessColumn = request.FreshnessColumn?.Trim();
        check.MaxAgeMinutes = request.MaxAgeMinutes > 0 ? request.MaxAgeMinutes : 1440;
        check.IsEnabled = request.IsEnabled;
        check.ModifiedBy = modifiedBy;
        check.ModifiedOn = now;

        await _context.SaveChangesAsync(ct);

        return new TableCheckDto
        {
            EntityId = entityId,
            FreshnessColumn = check.FreshnessColumn,
            MaxAgeMinutes = check.MaxAgeMinutes,
            IsEnabled = check.IsEnabled,
            HasDatabaseConnection = await HasParentConnectionAsync(entityId, companyId, ct)
        };
    }

    public async Task<bool> DeleteTableCheckAsync(string entityId, string companyId, CancellationToken ct = default)
    {
        var check = await _context.TableChecks
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);
        if (check == null) return false;

        _context.TableChecks.Remove(check);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<TableFreshnessResult> RunFreshnessCheckAsync(string entityId, string companyId, CancellationToken ct = default)
    {
        var check = await _context.TableChecks.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);
        if (check == null || string.IsNullOrWhiteSpace(check.FreshnessColumn))
            return new TableFreshnessResult { Ok = false, Error = "Configure a freshness column and save it first." };

        var table = await _context.MonitoredAssets.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, ct);
        if (table == null)
            return new TableFreshnessResult { Ok = false, Error = "Table entity not found." };

        var connection = await GetDecryptedParentConnectionAsync(entityId, companyId, ct);
        if (connection == null)
            return new TableFreshnessResult { Ok = false, Error = "No database connection found on the parent database entity. Configure it there first." };

        return await CheckFreshnessAsync(connection, table.Name, check.FreshnessColumn!, check.MaxAgeMinutes, ct);
    }

    // ---- Bulk read for ingestion (no DbContext) ----

    // A generous default so large (streaming) pulls don't trip the provider's short default timeout.
    // 0 means "no timeout". Callers can override per ingestion source.
    private const int DefaultReadCommandTimeoutSeconds = 3600;

    public async Task<int> ReadToTempCsvAsync(DatabaseConnection c, string query, string destCsvPath, CancellationToken ct = default, int? commandTimeoutSeconds = null, IProgress<long>? rowProgress = null)
    {
        // ClickHouse has no ADO driver — ask it for CSV directly over the read-only HTTP endpoint.
        if (c.DatabaseType == DataSourceType.ClickHouse)
        {
            var csvQuery = $"{query.TrimEnd().TrimEnd(';')} FORMAT CSVWithNames";
            var body = await QueryClickHouseAsync(c, csvQuery, ct);
            await File.WriteAllTextAsync(destCsvPath, body, new UTF8Encoding(false), ct);
            // Row count = lines minus the header (best-effort).
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            var count = Math.Max(0, lines - 1);
            rowProgress?.Report(count);
            return count;
        }

        await using var connection = CreateAdoConnection(c);
        await connection.OpenAsync(ct);
        await ApplyReadOnlySetupAsync(connection, c, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = commandTimeoutSeconds ?? DefaultReadCommandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(ct);

        await using var writer = new StreamWriter(destCsvPath, false, new UTF8Encoding(false));
        await WriteCsvHeaderAsync(reader, writer);
        return await WriteCsvRowsAsync(reader, writer, ct, rowProgress);
    }

    public async Task<int> ReadToTempCsvBatchedAsync(DatabaseConnection c, string baseQuery, string keyColumn, int batchSize, string destCsvPath, CancellationToken ct = default, int? commandTimeoutSeconds = null, IProgress<long>? rowProgress = null)
    {
        if (batchSize <= 0)
            return await ReadToTempCsvAsync(c, baseQuery, destCsvPath, ct, commandTimeoutSeconds, rowProgress);
        if (c.DatabaseType == DataSourceType.ClickHouse)
            // ClickHouse has no ADO reader here; page it over the HTTP CSV endpoint with ORDER BY + LIMIT/OFFSET.
            return await ReadClickHouseBatchedAsync(c, baseQuery, keyColumn, batchSize, destCsvPath, ct, commandTimeoutSeconds, rowProgress);

        await using var connection = CreateAdoConnection(c);
        await connection.OpenAsync(ct);
        await ApplyReadOnlySetupAsync(connection, c, ct);

        // The batch key may be a single column or a comma-separated composite (e.g. company_id,item_no,
        // variant_code). Keyset paging orders by the full key and advances with a lexicographic '>' over
        // all parts, so a composite key is safe even when no single column is unique — as long as the
        // columns together are unique.
        var keyColumns = keyColumn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (keyColumns.Count == 0)
            return await ReadToTempCsvAsync(c, baseQuery, destCsvPath, ct, commandTimeoutSeconds, rowProgress);

        await using var writer = new StreamWriter(destCsvPath, false, new UTF8Encoding(false));
        var timeout = commandTimeoutSeconds ?? DefaultReadCommandTimeoutSeconds;
        var totalRows = 0;
        var headerWritten = false;
        List<string>? lastKeyLiterals = null;

        while (true)
        {
            var pageSql = BuildKeysetPageSql(c.DatabaseType, baseQuery, keyColumns, batchSize, lastKeyLiterals);

            await using var command = connection.CreateCommand();
            command.CommandText = pageSql;
            command.CommandTimeout = timeout;
            await using var reader = await command.ExecuteReaderAsync(ct);

            if (!headerWritten)
            {
                await WriteCsvHeaderAsync(reader, writer);
                headerWritten = true;
            }

            // Resolve each key column to its ordinal in the result (must be among the selected columns).
            var keyOrdinals = new int[keyColumns.Count];
            for (int k = 0; k < keyColumns.Count; k++)
            {
                keyOrdinals[k] = FindOrdinal(reader, keyColumns[k]);
                if (keyOrdinals[k] < 0)
                    throw new InvalidOperationException($"Batch key column '{keyColumns[k]}' was not found in the source result. It must be one of the selected columns.");
            }

            var fieldCount = reader.FieldCount;
            var pageRows = 0;
            object?[]? lastKeyValues = null;
            while (await reader.ReadAsync(ct))
            {
                for (int i = 0; i < fieldCount; i++)
                {
                    if (i > 0) await writer.WriteAsync(',');
                    if (!reader.IsDBNull(i))
                        await writer.WriteAsync(CsvEscape(FormatCsvValue(reader.GetValue(i))));
                }
                await writer.WriteAsync('\n');

                // Remember this row's key values as the cursor for the next page. A null in any key part
                // means we can't form a reliable cursor from this row, so keep the last fully-valued one.
                var keyValues = new object?[keyColumns.Count];
                var anyNull = false;
                for (int k = 0; k < keyColumns.Count; k++)
                {
                    if (reader.IsDBNull(keyOrdinals[k])) { anyNull = true; break; }
                    keyValues[k] = reader.GetValue(keyOrdinals[k]);
                }
                if (!anyNull) lastKeyValues = keyValues;

                pageRows++;
                totalRows++;
            }

            rowProgress?.Report(totalRows);

            // Last (smaller) page, or we can't advance the keyset cursor → done.
            if (pageRows < batchSize || lastKeyValues == null)
                break;
            lastKeyLiterals = lastKeyValues.Select(v => KeyLiteral(v!)).ToList();
        }

        return totalRows;
    }

    // Writes all ClickHouse pages into a single CSV file (used by the fetch-to-temp-then-import path).
    private async Task<int> ReadClickHouseBatchedAsync(DatabaseConnection c, string baseQuery, string keyColumn, int batchSize, string destCsvPath, CancellationToken ct, int? commandTimeoutSeconds, IProgress<long>? rowProgress)
    {
        await using var writer = new StreamWriter(destCsvPath, false, new UTF8Encoding(false));
        var headerWritten = false;
        var total = 0;

        return await PageClickHouseCsvAsync(c, baseQuery, keyColumn, batchSize, async (pageCsv, pageRows) =>
        {
            var firstNl = pageCsv.IndexOf('\n');
            if (!headerWritten)
            {
                await writer.WriteAsync(pageCsv.Substring(0, firstNl + 1));
                headerWritten = true;
            }
            var rowsPart = pageCsv.Substring(firstNl + 1);
            if (rowsPart.Length > 0) await writer.WriteAsync(rowsPart);
            total += pageRows;
            rowProgress?.Report(total);
        }, ct, commandTimeoutSeconds);
    }

    /// <summary>
    /// Pages a ClickHouse query over the read-only HTTP CSV endpoint and invokes <paramref name="onPage"/> for
    /// each page as it arrives (each call receives the full page CSV <b>including the header line</b>, and the
    /// page's row count). Lets callers import batch-by-batch instead of buffering the whole result. Returns the
    /// total row count. See <see cref="PageClickHouseCsvAsync"/> for the ordering caveat.
    /// </summary>
    public Task<int> ReadClickHouseBatchesAsync(DatabaseConnection c, string baseQuery, string keyColumn, int batchSize, Func<string, int, Task> onPage, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        => PageClickHouseCsvAsync(c, baseQuery, keyColumn, batchSize, onPage, ct, commandTimeoutSeconds);

    // ClickHouse paging over the read-only HTTP CSV endpoint. Keyset paging isn't practical here (we can't
    // read the last key back out of the CSV stream cheaply), so we page with a stable ORDER BY + LIMIT/OFFSET.
    // The batch key should therefore be as unique/stable as possible (a composite of columns that together are
    // unique is ideal) — ties on the ordering columns can shift rows across page boundaries. Each page's CSV
    // (with header) is handed to onPage(pageCsv, pageRows).
    private async Task<int> PageClickHouseCsvAsync(DatabaseConnection c, string baseQuery, string keyColumn, int batchSize, Func<string, int, Task> onPage, CancellationToken ct, int? commandTimeoutSeconds)
    {
        var trimmed = baseQuery.TrimEnd().TrimEnd(';');
        // The batch key may be a single column or a comma-separated combination (e.g. a composite id).
        // Ordering by the full combination gives a deterministic total order, which is all LIMIT/OFFSET
        // paging needs to avoid overlapping/missing rows across pages.
        var orderBy = string.Join(", ", keyColumn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => $"_src.`{k.Replace("`", "")}`"));

        var total = 0;
        var offset = 0;

        while (true)
        {
            var pageSql = $"SELECT * FROM ({trimmed}) AS _src ORDER BY {orderBy} LIMIT {batchSize} OFFSET {offset} FORMAT CSVWithNames";
            var body = await QueryClickHouseAsync(c, pageSql, ct);
            if (string.IsNullOrEmpty(body)) break;

            // First line is the CSV header (plain column names — no quotes/embedded newlines).
            var firstNl = body.IndexOf('\n');
            if (firstNl < 0) break; // header-only / malformed page

            var pageRows = CountCsvRecords(body.Substring(firstNl + 1));
            if (pageRows > 0) await onPage(body, pageRows);
            total += pageRows;

            if (pageRows < batchSize) break; // short (final) page
            offset += batchSize;
        }

        return total;
    }

    // Counts CSV records (newline-terminated), ignoring newlines inside quoted fields. Doubled quotes ("")
    // toggle twice → net no change, which is fine since we only care about record-terminating newlines.
    private static int CountCsvRecords(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return 0;
        var count = 0;
        var inQuotes = false;
        foreach (var ch in csv)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == '\n' && !inQuotes) count++;
        }
        if (csv[^1] != '\n') count++; // trailing record without a final newline
        return count;
    }

    private async Task ApplyReadOnlySetupAsync(DbConnection connection, DatabaseConnection c, CancellationToken ct)
    {
        var readOnlySetup = ReadOnlySetupFor(c.DatabaseType);
        if (string.IsNullOrEmpty(readOnlySetup)) return;
        await using var setup = connection.CreateCommand();
        setup.CommandText = readOnlySetup;
        await setup.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteCsvHeaderAsync(DbDataReader reader, StreamWriter writer)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) await writer.WriteAsync(',');
            await writer.WriteAsync(CsvEscape(reader.GetName(i)));
        }
        await writer.WriteAsync('\n');
    }

    private const int RowProgressInterval = 50_000;

    private static async Task<int> WriteCsvRowsAsync(DbDataReader reader, StreamWriter writer, CancellationToken ct, IProgress<long>? rowProgress = null)
    {
        var fieldCount = reader.FieldCount;
        var rows = 0;
        while (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < fieldCount; i++)
            {
                if (i > 0) await writer.WriteAsync(',');
                if (!reader.IsDBNull(i))
                    await writer.WriteAsync(CsvEscape(FormatCsvValue(reader.GetValue(i))));
            }
            await writer.WriteAsync('\n');
            rows++;
            if (rowProgress != null && rows % RowProgressInterval == 0)
                rowProgress.Report(rows);
        }
        if (rowProgress != null && rows % RowProgressInterval != 0)
            rowProgress.Report(rows); // final count
        return rows;
    }

    private static int FindOrdinal(DbDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // Builds one ordered keyset page over an arbitrary base query. Orders by the full (possibly composite)
    // key and, after the first page, filters to rows strictly greater than the previous page's last key
    // using a lexicographic comparison expanded into OR/AND terms — portable across SQL Server (which has
    // no row-value comparison) as well as Postgres/MySQL/DuckDB. Strict '>' on the whole key means the key
    // must be unique together; a non-unique composite still risks skipping rows sharing the boundary key.
    private static string BuildKeysetPageSql(DataSourceType type, string baseQuery, IReadOnlyList<string> keyColumns, int batchSize, IReadOnlyList<string>? lastKeyLiterals)
    {
        var cols = keyColumns.Select(k => QuoteIdentifier(type, k)).ToList();
        var inner = baseQuery.TrimEnd().TrimEnd(';');
        var order = " ORDER BY " + string.Join(", ", cols.Select(c => $"_src.{c} ASC"));

        var where = string.Empty;
        if (lastKeyLiterals != null && lastKeyLiterals.Count == cols.Count)
        {
            // (c1 > v1) OR (c1 = v1 AND c2 > v2) OR (c1 = v1 AND c2 = v2 AND c3 > v3) ...
            var terms = new List<string>();
            for (int i = 0; i < cols.Count; i++)
            {
                var parts = new List<string>();
                for (int j = 0; j < i; j++)
                    parts.Add($"_src.{cols[j]} = {lastKeyLiterals[j]}");
                parts.Add($"_src.{cols[i]} > {lastKeyLiterals[i]}");
                terms.Add(parts.Count == 1 ? parts[0] : "(" + string.Join(" AND ", parts) + ")");
            }
            where = " WHERE " + string.Join(" OR ", terms);
        }

        return type switch
        {
            DataSourceType.SQLServer or DataSourceType.PostgreSQL =>
                $"SELECT * FROM ({inner}) _src{where}{order} OFFSET 0 ROWS FETCH NEXT {batchSize} ROWS ONLY",
            _ => // MySQL, DuckDB
                $"SELECT * FROM ({inner}) _src{where}{order} LIMIT {batchSize}"
        };
    }

    // Formats a key value as a SQL literal for the keyset WHERE clause (numbers bare, dates/strings quoted).
    private static string KeyLiteral(object value) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? "1" : "0",
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal or double or float
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fffffff}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fffffff}'",
        Guid g => $"'{g}'",
        _ => "'" + (value.ToString() ?? string.Empty).Replace("'", "''") + "'"
    };

    // Formats a value for a CSV cell in a way DuckDB can TRY_CAST back to a typed column.
    private static string FormatCsvValue(object value) => value switch
    {
        null or DBNull => string.Empty,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    // RFC-4180 quoting: wrap in quotes and double internal quotes when the value contains , " CR or LF.
    private static string CsvEscape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // ---- Pure probes (no DbContext; safe to call concurrently from the ping job) ----

    public async Task<DatabaseProbeResult> ProbeConnectionAsync(DatabaseConnection c, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (c.DatabaseType == DataSourceType.ClickHouse)
                await QueryClickHouseAsync(c, "SELECT 1", ct);
            else
                await ExecuteScalarAsync(CreateAdoConnection(c), "SELECT 1", ReadOnlySetupFor(c.DatabaseType), ct);

            sw.Stop();
            return new DatabaseProbeResult { Ok = true, ResponseMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2) };
        }
        catch (Exception ex)
        {
            return new DatabaseProbeResult { Ok = false, Error = Truncate(ex.Message) };
        }
    }

    public async Task<TableFreshnessResult> CheckFreshnessAsync(DatabaseConnection c, string tableFullName, string freshnessColumn, int maxAgeMinutes, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            object? maxVal;
            long rowCount;

            if (c.DatabaseType == DataSourceType.ClickHouse)
                (maxVal, rowCount) = await QueryClickHouseFreshnessAsync(c, tableFullName, freshnessColumn, ct);
            else
                (maxVal, rowCount) = await QueryFreshnessAdoAsync(
                    CreateAdoConnection(c),
                    BuildFreshnessSql(c.DatabaseType, tableFullName, freshnessColumn),
                    ReadOnlySetupFor(c.DatabaseType), ct);

            sw.Stop();

            var result = new TableFreshnessResult
            {
                Ok = true,
                ResponseMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                RowCount = rowCount,
                LastUpdatedUtc = CoerceToUtc(maxVal)
            };

            if (result.LastUpdatedUtc.HasValue)
            {
                var age = (DateTime.UtcNow - result.LastUpdatedUtc.Value).TotalMinutes;
                result.AgeMinutes = Math.Round(age, 2);
                result.IsStale = age > maxAgeMinutes;
            }

            return result;
        }
        catch (Exception ex)
        {
            return new TableFreshnessResult { Ok = false, Error = Truncate(ex.Message) };
        }
    }

    public Task<DatabaseConnection?> GetDecryptedConnectionAsync(string entityId, string companyId, CancellationToken ct = default)
        => LoadDecryptedAsync(entityId, companyId, ct);

    public async Task<DatabaseConnection?> GetDecryptedParentConnectionAsync(string tableEntityId, string companyId, CancellationToken ct = default)
    {
        var parentId = await GetParentDatabaseEntityIdAsync(tableEntityId, companyId, ct);
        return parentId == null ? null : await LoadDecryptedAsync(parentId, companyId, ct);
    }

    // ---- Internals ----

    /// <summary>The id of the Database entity a Table entity depends on (the "Table in database" edge).</summary>
    private async Task<string?> GetParentDatabaseEntityIdAsync(string tableEntityId, string companyId, CancellationToken ct) =>
        await _context.AssetDependencies
            .Where(d => d.EntityId == tableEntityId && d.CompanyId == companyId
                        && d.DependencyType == AssetType.Database && d.IsActive)
            .Select(d => d.DependsOnEntityId)
            .FirstOrDefaultAsync(ct);

    private async Task<bool> HasParentConnectionAsync(string tableEntityId, string companyId, CancellationToken ct)
    {
        var parentId = await GetParentDatabaseEntityIdAsync(tableEntityId, companyId, ct);
        return parentId != null &&
               await _context.DatabaseConnections.AnyAsync(c => c.EntityId == parentId && c.CompanyId == companyId, ct);
    }

    private async Task<DatabaseConnection?> LoadDecryptedAsync(string entityId, string companyId, CancellationToken ct)
    {
        var connection = await _context.DatabaseConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.CompanyId == companyId, ct);
        if (connection == null) return null;

        // Decrypt the secret in-memory for execution; never persist or return this to the client.
        if (!string.IsNullOrEmpty(connection.SecretEncrypted))
            connection.SecretEncrypted = _protector.Decrypt(connection.SecretEncrypted);

        return connection;
    }

    /// <summary>Dispatches to the engine-specific table listing. The password is read from
    /// <see cref="DatabaseConnection.SecretEncrypted"/> which the caller has decrypted in-memory.</summary>
    private async Task<List<(string Schema, string Name)>> ListTablesAsync(DatabaseConnection c, CancellationToken ct)
    {
        // Every path is strictly read-only: only SELECT against metadata, and the session/connection
        // is opened read-only where the engine supports it (readOnlySetup runs before the listing query).
        if (c.DatabaseType == DataSourceType.ClickHouse)
            return await QueryClickHouseTablesAsync(c, ct);

        var sql = c.DatabaseType switch
        {
            DataSourceType.SQLServer => "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'",
            DataSourceType.PostgreSQL => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')",
            DataSourceType.MySQL => $"SELECT table_schema, table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema = '{EscapeLiteral(c.DatabaseName)}'",
            DataSourceType.DuckDB => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE'",
            _ => throw new NotSupportedException($"Unsupported database type: {c.DatabaseType}.")
        };

        return await QueryTablesAsync(CreateAdoConnection(c), sql, ct, ReadOnlySetupFor(c.DatabaseType));
    }

    /// <summary>Builds an unopened ADO.NET connection for an engine. ClickHouse is HTTP-only (no ADO).</summary>
    private static DbConnection CreateAdoConnection(DatabaseConnection c) => c.DatabaseType switch
    {
        DataSourceType.SQLServer => new SqlConnection(BuildSqlServerConnectionString(c)),
        DataSourceType.PostgreSQL => new NpgsqlConnection(BuildPostgresConnectionString(c)),
        DataSourceType.MySQL => new MySqlConnection(BuildMySqlConnectionString(c)),
        DataSourceType.DuckDB => new DuckDBConnection(BuildDuckDbConnectionString(c)),
        _ => throw new NotSupportedException($"No ADO.NET driver for database type: {c.DatabaseType}.")
    };

    /// <summary>Command to put the session into read-only mode before querying, where the engine supports it.</summary>
    private static string? ReadOnlySetupFor(DataSourceType type) => type switch
    {
        DataSourceType.PostgreSQL => "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY",
        DataSourceType.MySQL => "SET SESSION TRANSACTION READ ONLY",
        _ => null
    };

    /// <summary>Opens an ADO.NET connection, runs a two-column (schema, name) query, and returns the rows.
    /// <paramref name="readOnlySetup"/>, when set, runs first to put the session into read-only mode.</summary>
    private static async Task<List<(string Schema, string Name)>> QueryTablesAsync(DbConnection connection, string sql, CancellationToken ct, string? readOnlySetup = null)
    {
        var tables = new List<(string, string)>();
        await using (connection)
        {
            await connection.OpenAsync(ct);

            if (!string.IsNullOrEmpty(readOnlySetup))
            {
                await using var setup = connection.CreateCommand();
                setup.CommandText = readOnlySetup;
                await setup.ExecuteNonQueryAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var schema = reader.IsDBNull(0) ? string.Empty : reader.GetValue(0)?.ToString() ?? string.Empty;
                var name = reader.IsDBNull(1) ? string.Empty : reader.GetValue(1)?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(name))
                    tables.Add((schema, name));
            }
        }
        return tables;
    }

    /// <summary>Runs a read-only ClickHouse query over HTTP (readonly=1) and returns the raw response body.</summary>
    private async Task<string> QueryClickHouseAsync(DatabaseConnection c, string query, CancellationToken ct)
    {
        var protocol = c.UseSsl ? "https" : "http";
        // readonly=1 makes the ClickHouse session reject any write/DDL — these queries only need reads.
        var url = $"{protocol}://{c.Host}:{c.Port}/?readonly=1";

        var client = _httpClientFactory.CreateClient();
        // Ingestion reads can run for minutes on large ClickHouse queries; the default 100s HttpClient
        // timeout aborts them mid-flight. Allow up to 30 minutes (the caller's CancellationToken still applies).
        client.Timeout = TimeSpan.FromMinutes(30);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(c.Username))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.SecretEncrypted}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        request.Content = new StringContent(query, Encoding.UTF8, "text/plain");

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ClickHouse query failed ({(int)response.StatusCode}): {body}");
        return body;
    }

    private async Task<List<(string Schema, string Name)>> QueryClickHouseTablesAsync(DatabaseConnection c, CancellationToken ct)
    {
        // Scope to the configured database when one is set, so a dataset backed by (say) `sales_dataset`
        // lists only that database's tables. ClickHouse's HTTP endpoint has no per-connection default
        // catalog — unlike SQL Server/Postgres (which connect to a specific catalog) or MySQL (which we
        // filter by table_schema) — so without this filter system.tables returns every non-system database.
        var whereClause = string.IsNullOrWhiteSpace(c.DatabaseName)
            ? "database NOT IN ('system', 'INFORMATION_SCHEMA', 'information_schema')"
            : $"database = '{EscapeLiteral(c.DatabaseName)}'";
        var query = $"SELECT database, name FROM system.tables WHERE {whereClause} FORMAT JSONEachRow";
        var body = await QueryClickHouseAsync(c, query, ct);

        var tables = new List<(string, string)>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (row == null) continue;
                var schema = row.TryGetValue("database", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                var name = row.TryGetValue("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(name))
                    tables.Add((schema, name));
            }
            catch
            {
                // Skip malformed lines.
            }
        }
        return tables;
    }

    /// <summary>Opens a connection read-only and runs a scalar query (e.g. SELECT 1), discarding the result.</summary>
    private static async Task ExecuteScalarAsync(DbConnection connection, string sql, string? readOnlySetup, CancellationToken ct)
    {
        await using (connection)
        {
            await connection.OpenAsync(ct);

            if (!string.IsNullOrEmpty(readOnlySetup))
            {
                await using var setup = connection.CreateCommand();
                setup.CommandText = readOnlySetup;
                await setup.ExecuteNonQueryAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteScalarAsync(ct);
        }
    }

    /// <summary>Runs the two-column freshness query and returns (MAX value, row count) from the first row.</summary>
    private static async Task<(object? MaxValue, long RowCount)> QueryFreshnessAdoAsync(DbConnection connection, string sql, string? readOnlySetup, CancellationToken ct)
    {
        await using (connection)
        {
            await connection.OpenAsync(ct);

            if (!string.IsNullOrEmpty(readOnlySetup))
            {
                await using var setup = connection.CreateCommand();
                setup.CommandText = readOnlySetup;
                await setup.ExecuteNonQueryAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var maxVal = reader.IsDBNull(0) ? null : reader.GetValue(0);
                var rowCount = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
                return (maxVal, rowCount);
            }
            return (null, 0L);
        }
    }

    private async Task<(object? MaxValue, long RowCount)> QueryClickHouseFreshnessAsync(DatabaseConnection c, string tableFullName, string column, CancellationToken ct)
    {
        var table = QuoteQualified(DataSourceType.ClickHouse, tableFullName);
        var col = QuoteIdentifier(DataSourceType.ClickHouse, column);
        // toString() so JSONEachRow yields plain strings we can parse uniformly across column types.
        var query = $"SELECT toString(max({col})) AS last_updated, toString(count()) AS row_count FROM {table} FORMAT JSONEachRow";

        var body = await QueryClickHouseAsync(c, query, ct);
        var line = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line == null) return (null, 0L);

        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
        object? maxVal = null;
        var rowCount = 0L;
        if (row != null)
        {
            if (row.TryGetValue("last_updated", out var m))
            {
                var s = m.GetString();
                maxVal = string.IsNullOrEmpty(s) ? null : s;
            }
            if (row.TryGetValue("row_count", out var rc) && long.TryParse(rc.GetString(), out var parsed))
                rowCount = parsed;
        }
        return (maxVal, rowCount);
    }

    private static string BuildFreshnessSql(DataSourceType type, string tableFullName, string column)
    {
        var table = QuoteQualified(type, tableFullName);
        var col = QuoteIdentifier(type, column);
        // SQL Server's COUNT(*) is int and overflows past ~2.1B rows; COUNT_BIG(*) is bigint.
        var countExpr = type == DataSourceType.SQLServer ? "COUNT_BIG(*)" : "COUNT(*)";
        return $"SELECT MAX({col}) AS last_updated, {countExpr} AS row_count FROM {table}";
    }

    /// <summary>Quotes a possibly-qualified "{schema}.{table}" name, splitting on the first dot.</summary>
    private static string QuoteQualified(DataSourceType type, string fullName)
    {
        var idx = fullName.IndexOf('.');
        if (idx <= 0 || idx == fullName.Length - 1)
            return QuoteIdentifier(type, fullName);
        return $"{QuoteIdentifier(type, fullName[..idx])}.{QuoteIdentifier(type, fullName[(idx + 1)..])}";
    }

    /// <summary>Quotes a single identifier for the engine, escaping the quote char (neutralizes injection).</summary>
    private static string QuoteIdentifier(DataSourceType type, string id) => type switch
    {
        DataSourceType.SQLServer => $"[{id.Replace("]", "]]")}]",
        DataSourceType.PostgreSQL => $"\"{id.Replace("\"", "\"\"")}\"",
        DataSourceType.DuckDB => $"\"{id.Replace("\"", "\"\"")}\"",
        DataSourceType.MySQL => $"`{id.Replace("`", "``")}`",
        DataSourceType.ClickHouse => $"`{id.Replace("`", "``")}`",
        _ => id
    };

    /// <summary>Best-effort coercion of a MAX(timestamp) value to UTC. Timestamps are assumed to be UTC.</summary>
    private static DateTime? CoerceToUtc(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case DateTime dt:
                return dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            case DateTimeOffset dto:
                return dto.UtcDateTime;
            default:
                return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : null;
        }
    }

    private static string Truncate(string s) => string.IsNullOrEmpty(s) || s.Length <= 300 ? s : s[..300];

    private static string BuildSqlServerConnectionString(DatabaseConnection c)
    {
        var server = c.Port > 0 ? $"{c.Host},{c.Port}" : c.Host;
        // ApplicationIntent=ReadOnly signals read-only intent (and routes to a readable secondary on AlwaysOn);
        // combined with SELECT-only queries the feature never writes. Harmless/ignored on standalone servers.
        return $"Server={server};Initial Catalog={c.DatabaseName};User ID={c.Username};Password={c.SecretEncrypted};" +
               $"Encrypt={(c.UseSsl ? "True" : "False")};TrustServerCertificate=True;ApplicationIntent=ReadOnly;Connection Timeout=15;";
    }

    private static string BuildPostgresConnectionString(DatabaseConnection c) =>
        $"Host={c.Host};Port={(c.Port > 0 ? c.Port : 5432)};Database={c.DatabaseName};Username={c.Username};Password={c.SecretEncrypted};" +
        $"SSL Mode={(c.UseSsl ? "Require" : "Prefer")};Trust Server Certificate=true;Timeout=15;";

    private static string BuildMySqlConnectionString(DatabaseConnection c) =>
        $"Server={c.Host};Port={(c.Port > 0 ? c.Port : 3306)};Database={c.DatabaseName};User ID={c.Username};Password={c.SecretEncrypted};" +
        $"SslMode={(c.UseSsl ? "Required" : "Preferred")};Connection Timeout=15;";

    private static string BuildDuckDbConnectionString(DatabaseConnection c) =>
        // READ_ONLY opens the file without taking a write lock and rejects any modification.
        $"DataSource={c.FilePath};ACCESS_MODE=READ_ONLY";

    private static string EscapeLiteral(string? value) => (value ?? string.Empty).Replace("'", "''");

    private static int DefaultPort(DataSourceType type) => type switch
    {
        DataSourceType.SQLServer => 1433,
        DataSourceType.PostgreSQL => 5432,
        DataSourceType.MySQL => 3306,
        DataSourceType.ClickHouse => 8123,
        _ => 0
    };

    private static DatabaseConnectionDto ToDto(DatabaseConnection c) => new()
    {
        Id = c.Id,
        EntityId = c.EntityId,
        DatabaseType = c.DatabaseType,
        Host = c.Host,
        Port = c.Port,
        DatabaseName = c.DatabaseName,
        Username = c.Username,
        UseSsl = c.UseSsl,
        FilePath = c.FilePath,
        HasSecret = !string.IsNullOrEmpty(c.SecretEncrypted)
    };
}

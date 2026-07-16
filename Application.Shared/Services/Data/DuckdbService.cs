using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Azure.Core;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Shared.Services.Data;

public class DuckdbService : IDuckdbService
{
    private readonly DuckdbOption _option;
    private readonly ApplicationDbContext _db;

    // Per-request (scoped) cache of datasetId -> storage directory, so a loop over many tables/datasets
    // doesn't re-query the dataset row for every DuckDB call. Safe because path changes happen in a
    // separate request; this instance never outlives one.
    private readonly Dictionary<string, string> _dirCache = new();

    public DuckdbService(DuckdbOption option, ApplicationDbContext db)
    {
        _option = option;
        _db = db;
    }

    public DatasetService? DatasetService { get; set; }

    // ---- Per-dataset storage path resolution -------------------------------------------------------
    // Each dataset stores its files under its own Path (chosen at create/edit); when unset we fall back
    // to the app-wide Duckdb:DuckdbFilePath. The DuckDB file itself is always "{dir}/{datasetId}.duckdb".

    /// <summary>The storage directory for a dataset id, from the dataset's Path (else the app default).</summary>
    private string ResolveDir(string datasetId)
    {
        if (string.IsNullOrWhiteSpace(datasetId)) return NormalizeDir(_option.DuckdbFilePath);
        if (_dirCache.TryGetValue(datasetId, out var cached)) return cached;

        string dir;
        try
        {
            var path = _db.Dataset.AsNoTracking()
                .Where(d => d.Id == datasetId)
                .Select(d => d.Path)
                .FirstOrDefault();
            dir = string.IsNullOrWhiteSpace(path) ? _option.DuckdbFilePath : path!;
        }
        catch
        {
            // Column not deployed yet / lookup failed — never hard-break DuckDB over a schema lag.
            dir = _option.DuckdbFilePath;
        }

        dir = NormalizeDir(dir);
        _dirCache[datasetId] = dir;
        return dir;
    }

    private static string DirFor(Dataset dataset, string fallback)
        => NormalizeDir(string.IsNullOrWhiteSpace(dataset.Path) ? fallback : dataset.Path!);

    // A user-typed path can carry trailing spaces or a trailing separator ("C:/duckdb/", "C:/duckdb ")
    // that would make "{dir}/{id}.duckdb" miss the file — trim both so resolution is exact.
    private static string NormalizeDir(string dir) => (dir ?? string.Empty).Trim().TrimEnd('/', '\\');

    /// <summary>The full DuckDB file path for a dataset id.</summary>
    private string ResolveDbPath(string datasetId) => $"{ResolveDir(datasetId)}/{datasetId}.duckdb";

    /// <summary>The full DuckDB file path for a loaded dataset (no DB lookup — uses its own Path).</summary>
    private string ResolveDbPath(Dataset dataset) => $"{DirFor(dataset, _option.DuckdbFilePath)}/{dataset.Id}.duckdb";

    public async Task CreateDatabaseAsync(Dataset dataset)
    {
        var dir = DirFor(dataset, _option.DuckdbFilePath);
        var duckdbFilePath = ResolveDbPath(dataset);

        if (File.Exists(duckdbFilePath))
            throw new InvalidOperationException("Database already exists.");

        // Ensure the chosen directory exists — a user-picked path may not exist yet.
        Directory.CreateDirectory(dir);

        // Create a new DuckDB database file
        // openning a connection will create the file
        using (var duckDBConnection = new DuckDBConnection($"Data Source={duckdbFilePath}"))
        {
            await duckDBConnection.OpenAsync();

            // close the connection
            await duckDBConnection.CloseAsync();
        }
    }

    public bool DatabaseExists(string datasetId)
        => File.Exists(ResolveDbPath(datasetId));

    public async Task EnsureDatabaseAsync(string datasetId)
    {
        var duckdbFilePath = ResolveDbPath(datasetId);

        if (File.Exists(duckdbFilePath))
            return;

        // Make sure the dataset's folder exists, then open a connection — that creates the file.
        Directory.CreateDirectory(ResolveDir(datasetId));
        using var duckDBConnection = new DuckDBConnection($"Data Source={duckdbFilePath}");
        await duckDBConnection.OpenAsync();
        await duckDBConnection.CloseAsync();
    }

    public async Task DeleteDatabaseAsync(Dataset dataset)
    {
        var duckdbFilePath = ResolveDbPath(dataset);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        await Task.Run(() => File.Delete(duckdbFilePath));
    }

    public async Task UpdateDatabaseAsync(string datasetId, string updateQuery)
    {

        var duckdbFilePath = ResolveDbPath(datasetId);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = updateQuery;

        await command.ExecuteNonQueryAsync();
        await connection.CloseAsync();
    }    
    
    public async Task<List<T>> ExecuteQueryAsync<T>(string databasePath, string query, Func<IDataReader, T> mapFunction)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException($"Database not found at '{databasePath}'.");

        var results = new List<T>();
        
        using var connection = new DuckDBConnection($"DataSource={databasePath}");
        await connection.OpenAsync();
        
        using var command = connection.CreateCommand();
        command.CommandText = query;
        
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(mapFunction(reader));
        }
        
        await connection.CloseAsync();
        return results;
    }

    public async Task<List<T>> ExecuteQueryAsync<T>(Dataset dataset, string query, Func<IDataReader, T> mapFunction)
    {
        var duckdbFilePath = ResolveDbPath(dataset);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        var results = new List<T>();
        
        using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
        await connection.OpenAsync();
        
        using var command = connection.CreateCommand();
        command.CommandText = query;
        
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(mapFunction(reader));
        }
        
        await connection.CloseAsync();
        return results;
    }

    public async Task<string> ExecuteQueryAsync(Dataset dataset, string query)
    {
        var duckdbFilePath = ResolveDbPath(dataset);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        // var results = new List<T>();

        using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = query;

        using var reader = await command.ExecuteReaderAsync();

        var results = new List<object>();
        while (await reader.ReadAsync())
        {
            // Assuming you want to return the first column of each row as a string
            if (reader.FieldCount > 0)
            {
                var value = reader.IsDBNull(0) ? "NULL" : reader.GetValue(0).ToString();
                results.Add(value);
            }
        }
        
        await connection.CloseAsync();

        // convert result to string
        return string.Join(", ", results.Select(r => r.ToString()));
    }

    public async Task<IEnumerable<string>> GetTablesAsync(string datasetId)
    {

        var duckdbFilePath = ResolveDbPath(datasetId);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        try
        {
            // Query to get all tables from the database
            var tablesQuery = @"
                SELECT table_name 
                FROM information_schema.tables 
                WHERE table_schema NOT IN ('information_schema', 'pg_catalog')
                ORDER BY table_name;";

            var tables = await ExecuteQueryAsync(duckdbFilePath, tablesQuery, reader => reader.GetString(0));

            return tables;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to retrieve tables for dataset '{datasetId}': {ex.Message}", ex);
        }
    }

    // ----- Storage stats (datasets list + tables list) --------------------------------------------

    public long GetDatabaseFileSize(string datasetId)
    {
        var path = ResolveDbPath(datasetId);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public async Task<(int TableCount, long TotalRows)> GetDatasetTableSummaryAsync(string datasetId, CancellationToken ct = default)
    {
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
            return (0, 0);

        try
        {
            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath};ACCESS_MODE=READ_ONLY");
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT COUNT(*), COALESCE(SUM(estimated_size), 0)
                                FROM duckdb_tables()
                                WHERE schema_name NOT IN ('information_schema', 'pg_catalog')";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var count = reader.IsDBNull(0) ? 0 : Convert.ToInt32(NormalizeValue(reader.GetValue(0)));
                var rows = reader.IsDBNull(1) ? 0L : Convert.ToInt64(NormalizeValue(reader.GetValue(1)));
                await connection.CloseAsync();
                return (count, rows);
            }
            await connection.CloseAsync();
        }
        catch { /* unreadable/locked db → report no stats rather than throwing on the list page */ }

        return (0, 0);
    }

    public async Task<List<TableStats>> GetTableStatsAsync(string datasetId, CancellationToken ct = default)
    {
        var result = new List<TableStats>();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
            return result;

        using var connection = new DuckDBConnection($"DataSource={duckdbFilePath};ACCESS_MODE=READ_ONLY");
        await connection.OpenAsync(ct);

        // One catalog query yields name + row estimate + column count + PK flag for every user table.
        var tables = new List<(string Name, long Rows, int Cols, bool HasPk)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"SELECT table_name, estimated_size, column_count, has_primary_key
                                FROM duckdb_tables()
                                WHERE schema_name NOT IN ('information_schema', 'pg_catalog')
                                ORDER BY table_name";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var rows = reader.IsDBNull(1) ? 0L : Convert.ToInt64(NormalizeValue(reader.GetValue(1)));
                var cols = reader.IsDBNull(2) ? 0 : Convert.ToInt32(NormalizeValue(reader.GetValue(2)));
                var hasPk = !reader.IsDBNull(3) && Convert.ToBoolean(NormalizeValue(reader.GetValue(3)));
                tables.Add((name, rows, cols, hasPk));
            }
        }

        // Column-type mix per table (text/num/date/bool/other) from one information_schema sweep.
        var typeSummaries = await ReadColumnTypeBucketsAsync(connection, ct);

        // DuckDB doesn't expose a per-table byte size directly (pragma_storage_info has no size column in
        // this version). Estimate it as (distinct data blocks the table occupies) × the database block
        // size — the block size comes from the storage catalog.
        long blockSize = 0;
        try
        {
            using var bs = connection.CreateCommand();
            bs.CommandText = "SELECT block_size FROM pragma_database_size()";
            var v = NormalizeValue(await bs.ExecuteScalarAsync(ct));
            blockSize = v is null ? 0 : Convert.ToInt64(v);
        }
        catch { /* no block size → sizes stay 0 */ }

        foreach (var t in tables)
        {
            long sizeBytes = 0;
            if (blockSize > 0)
            {
                try
                {
                    // Count the distinct blocks referenced by all of the table's column segments (including
                    // the overflow blocks in additional_block_ids), then multiply by the block size.
                    using var sizeCmd = connection.CreateCommand();
                    sizeCmd.CommandText = $@"
                        WITH s AS (SELECT * FROM pragma_storage_info('{t.Name.Replace("'", "''")}'))
                        SELECT COUNT(DISTINCT b) FROM (
                            SELECT block_id AS b FROM s WHERE block_id >= 0
                            UNION
                            SELECT UNNEST(additional_block_ids) AS b FROM s
                        )";
                    var normalized = NormalizeValue(await sizeCmd.ExecuteScalarAsync(ct));
                    var blocks = normalized is null ? 0 : Convert.ToInt64(normalized);
                    sizeBytes = blocks * blockSize;
                }
                catch { /* storage_info unavailable for this table → leave size at 0 */ }
            }

            result.Add(new TableStats
            {
                TableName = t.Name,
                RowCount = t.Rows,
                ColumnCount = t.Cols,
                SizeBytes = sizeBytes,
                HasPrimaryKey = t.HasPk,
                TypeSummary = typeSummaries.TryGetValue(t.Name, out var summary) ? summary : string.Empty
            });
        }

        await connection.CloseAsync();
        return result;
    }

    // Reads information_schema.columns once and returns, per table, a compact type-mix label like
    // "5 text · 3 num · 1 date" (buckets with a zero count are omitted).
    private static async Task<Dictionary<string, string>> ReadColumnTypeBucketsAsync(DuckDBConnection connection, CancellationToken ct)
    {
        var counts = new Dictionary<string, int[]>(); // table → [text, num, date, bool, other]
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"SELECT table_name, data_type
                                FROM information_schema.columns
                                WHERE table_schema NOT IN ('information_schema', 'pg_catalog')";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                var type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (!counts.TryGetValue(table, out var arr)) { arr = new int[5]; counts[table] = arr; }
                arr[TypeBucket(type)]++;
            }
        }

        var labels = new[] { "text", "num", "date", "bool", "other" };
        var result = new Dictionary<string, string>();
        foreach (var kv in counts)
        {
            var parts = new List<string>();
            for (int i = 0; i < kv.Value.Length; i++)
                if (kv.Value[i] > 0) parts.Add($"{kv.Value[i]} {labels[i]}");
            result[kv.Key] = string.Join(" · ", parts);
        }
        return result;
    }

    // Buckets a DuckDB column type into 0=text 1=num 2=date 3=bool 4=other.
    private static int TypeBucket(string dataType)
    {
        var t = (dataType ?? string.Empty).ToUpperInvariant();
        if (t.StartsWith("BOOL")) return 3;
        if (t.StartsWith("DATE") || t.StartsWith("TIME")) return 2;  // DATE, TIME, TIMESTAMP...
        if (t.Contains("CHAR") || t.Contains("TEXT") || t == "STRING" || t == "UUID" || t == "JSON") return 0;
        if (t.Contains("INT") || t.StartsWith("DEC") || t.StartsWith("NUMERIC") || t.StartsWith("DOUBLE")
            || t.StartsWith("FLOAT") || t.StartsWith("REAL") || t.StartsWith("HUGE")) return 1;
        return 4;
    }

    public async Task<Table> GetTableAsync(string datasetId, string tableName)
    {

        try
        {
            var duckdbFilePath = ResolveDbPath(datasetId);

            // Escape embedded quotes — this table name comes straight from the request route.
            var safeTableName = tableName.Replace("'", "''");

            // Query for exactly the requested table — this previously had its table_name filter commented
            // out (plus a stray trailing comma before FROM), so it always returned every table in the
            // dataset and FirstOrDefault() silently picked the alphabetically-first one instead of the
            // one actually requested.
            var tablesQuery = @$"SELECT table_name, table_schema
                                FROM information_schema.tables
                                WHERE table_schema NOT IN ('information_schema')
                                  AND table_catalog = '{datasetId}' -- name of the database (datasetId)
                                  AND table_name = '{safeTableName}'
                                ORDER BY table_name;";

            var tables = await ExecuteQueryAsync(duckdbFilePath, tablesQuery, reader => new
            {
                TableName = reader.GetString(0),
                SchemaName = reader.GetString(1) // Assuming you want to include schema name as well
            });
            var table = tables.FirstOrDefault();
            
            if (table == null)
                throw new InvalidOperationException($"Table '{tableName}' not found in dataset '{datasetId}'.");

            Table t = new Table
            {
                TableName = table.TableName,
                SchemaName = table.SchemaName, // Assuming you want to include schema name as well
                DatasetId = datasetId,
                //CompanyId = dataset.CompanyId, // TODO: get the companyId from the header or from the dataset
                Columns = await GetTableColumnsAsync(datasetId, tableName)
            };

            return t;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to retrieve table '{tableName}': {ex.Message}", ex);
        }
    }


    public async Task<Table> CreateTableAsync(Table table)
    {
        var duckdbFilePath = ResolveDbPath(table.DatasetId);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        try
        {
            // Generate DuckDB CREATE TABLE query
            var createTableQuery = GenerateCreateTableQuery(table);

            // Execute the query
            await UpdateDatabaseAsync(table.DatasetId, createTableQuery);

            return table;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create table '{table.TableName}': {ex.Message}", ex);
        }
    }



    public async Task<bool> DeleteTableAsync(string datasetId, string tableName)
    {

        try
        {


            // Generate DROP TABLE query
            var dropTableQuery = $"DROP TABLE IF EXISTS {tableName};";



            // Execute the query using the correct method signature
            await UpdateDatabaseAsync(datasetId, dropTableQuery);

            return true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to delete table '{tableName}': {ex.Message}", ex);
        }
    }

    // Moves a table's data from one dataset's DuckDB file to another's, preserving column types exactly
    // (a native DuckDB-to-DuckDB copy via ATTACH, not a CSV round-trip which would infer types from content).
    public async Task<MoveTableResult> MoveTableAsync(string sourceDatasetId, string tableName, string targetDatasetId, CancellationToken ct = default)
    {
        var result = new MoveTableResult();
        var sourceDbPath = ResolveDbPath(sourceDatasetId);
        var targetDbPath = ResolveDbPath(targetDatasetId);

        if (!File.Exists(sourceDbPath))
        {
            result.Error = "Source dataset database not found.";
            return result;
        }
        if (!File.Exists(targetDbPath))
        {
            result.Error = "Destination dataset database not found.";
            return result;
        }

        try
        {
            using var connection = new DuckDBConnection($"DataSource={sourceDbPath}");
            await connection.OpenAsync(ct);

            if (!await TableExistsAsync(connection, tableName, ct))
            {
                result.Error = $"Table \"{tableName}\" was not found in the source dataset.";
                return result;
            }

            var attachedPath = targetDbPath.Replace("\\", "\\\\").Replace("'", "''");
            await ExecAsync(connection, $"ATTACH '{attachedPath}' AS move_target", ct);

            try
            {
                if (await TableExistsInCatalogAsync(connection, "move_target", tableName, ct))
                {
                    result.Error = $"A table named \"{tableName}\" already exists in the destination dataset.";
                    return result;
                }

                // DuckDB can't hold a single transaction open across two attached databases ("a single
                // transaction can only write to a single attached database"), so these run as separate
                // autocommitted statements. Ordered CREATE-then-DROP so a failure between them leaves the
                // table duplicated (recoverable), never lost.
                await ExecAsync(connection, $"CREATE TABLE move_target.{Q(tableName)} AS SELECT * FROM {Q(tableName)}", ct);
                await ExecAsync(connection, $"DROP TABLE {Q(tableName)}", ct);

                result.Success = true;
            }
            finally
            {
                try { await ExecAsync(connection, "DETACH move_target", ct); } catch { /* best-effort cleanup */ }
            }

            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private static async Task<bool> TableExistsInCatalogAsync(DuckDBConnection connection, string catalogName, string tableName, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        // information_schema spans every attached catalog on this connection — it isn't itself
        // catalog-qualifiable (there's no "catalog.information_schema.tables"), so filter by table_catalog.
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_catalog = '{catalogName.Replace("'", "''")}' AND table_name = '{tableName.Replace("'", "''")}'";
        var value = await cmd.ExecuteScalarAsync(ct);
        var normalized = NormalizeValue(value);
        return normalized != null && Convert.ToInt64(normalized) > 0;
    }



    // create a function that returns the list of the columns from a table in the database
    public async Task<List<Column>> GetTableColumnsAsync(string datasetId, string tableName)
    {
        var duckdbFilePath = ResolveDbPath(datasetId);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        List<Column> columns = new List<Column>();

        using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");

        await connection.OpenAsync();

        using var command = connection.CreateCommand();

        command.CommandText = $"PRAGMA table_info('{tableName}')";

        
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // col 5 is nullable and the values are false or true , convert it to bool
            Column col = new Column()
            {
                Name = reader.GetString(1),
                DataType = reader.GetString(2),
                //IsNullable = reader.GetString(3).ToLower() == "true" ? true : false,
                IsNullable = reader.GetBoolean(3),
                DefaultValue = reader.IsDBNull(4) ? null : reader.GetString(5),
                //IsPrimaryKey = reader.GetString(5).ToLower() == "true" ? true : false,
                IsPrimaryKey = reader.GetBoolean(5)
            };
            columns.Add(col);

        }

        await connection.CloseAsync();
        
        return columns;
    }



    private string GenerateCreateTableQuery(Table table)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {table.SchemaName}.{table.TableName} ("
        );

        var columnDefinitions = table.Columns.Select(col =>
        {
            var definition = $"    {col.Name} {col.DataType}";

            if (!col.IsNullable)
                definition += " NOT NULL";

            if (!string.IsNullOrWhiteSpace(col.DefaultValue))
                definition += $" DEFAULT {col.DefaultValue}";

            return definition;
        });

        sb.AppendLine(string.Join(",\n", columnDefinitions));
        sb.Append(");");

        return sb.ToString();
    }

    public async Task<bool> ImportCsvDataAsync(string datasetId, string tableName, Stream csvStream)
    {
        var duckdbFilePath = ResolveDbPath(datasetId);

        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        try
        {
            // Save the CSV stream to a temporary file
            var tempCsvPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.csv");
            //string tempCsvPath = await CleanCsvStreamToTempFileAsync(csvStream, Encoding.GetEncoding("windows-1252"));


            //using (var fileStream = new FileStream(tempCsvPath, FileMode.Create, FileAccess.Write))
            //{
            //    await csvStream.CopyToAsync(fileStream);
            //}

            using (var reader = new StreamReader(csvStream, Encoding.GetEncoding("windows-1252")))
            using (var writer = new StreamWriter(tempCsvPath, false, Encoding.UTF8))
            {
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null)
                    {
                        // Replace problematic characters here
                        //line = line.Replace("\"", "_");   // This is causing error
                        line = line.Replace("&", "AND");  

                        await writer.WriteLineAsync(line);
                    }
                }
            }



            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync();

            using var command = connection.CreateCommand();

            // Use DuckDB's COPY command to import CSV data efficiently
            // This handles large files better than INSERT statements
            command.CommandText = $"COPY {tableName} FROM '{tempCsvPath.Replace("\\", "\\\\")}' (HEADER, DELIMITER ',', QUOTE '\"', ESCAPE '\"', SAMPLE_SIZE -1)"; //, IGNORE_ERRORS true

            //command.CommandText = $"""
            //        CREATE TABLE {tableName} AS 
            //        SELECT * 
            //        FROM read_csv_auto('{tempCsvPath.Replace("\\", "\\\\")}', 
            //                           HEADER=true, 
            //                           SAMPLE_SIZE=-1); -- Reads entire file for best type inference
            //        """;

            await command.ExecuteNonQueryAsync();
            await connection.CloseAsync();

            // Clean up temporary file
            if (File.Exists(tempCsvPath))
            {
                File.Delete(tempCsvPath);
            }

            return true;
        }        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to import CSV data into table '{tableName}': {ex.Message}", ex);
        }
    }

    public async Task<bool> ImportCsvDataAsync(string companyId, string datasetId, string tableName, Stream csvStream, bool createDataset = false, bool createTable = false)
    {
        var duckdbFilePath = ResolveDbPath(datasetId);

        if (createDataset)
        {
            // If the dataset does not exist, create it
            var dataset = new Dataset
            {
                Id = datasetId,
                Name = tableName, // You can set a more meaningful name
                CompanyId = companyId, // Set a default company ID or pass it as a parameter
                Description = "Imported dataset from CSV",
            };
            await CreateDatabaseAsync(dataset);
        }
        else
        {
            if (!File.Exists(duckdbFilePath))
                throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");
        }

        

        

        

        try
        {
            // Save the CSV stream to a temporary file
            var tempCsvPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.csv");

            using (var fileStream = new FileStream(tempCsvPath, FileMode.Create, FileAccess.Write))
            {
                await csvStream.CopyToAsync(fileStream);
            }

            if (createTable)
            {
                // If the table does not exist, create it based on the CSV structure
                //using var reader = new StreamReader(csvStream);

                // read the file from tempCsvPath
                using var reader = new StreamReader(tempCsvPath);

                var headerLine = await reader.ReadLineAsync();

                if (headerLine == null)
                    throw new InvalidOperationException("CSV file is empty.");

                // remove columns with no name

                var columns = headerLine.Split(',').Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => new Column
                    {
                        Name = c.Trim(),
                        //DataType = "VARCHAR", // Default to TEXT, you can enhance this to infer types
                        IsNullable = true,
                        DefaultValue = null,
                        IsPrimaryKey = false
                    }).ToList();

                var table = new Table
                {
                    TableName = tableName,
                    SchemaName = "main", // Default schema, can be parameterized
                    DatasetId = datasetId,
                    Columns = columns
                };
                await CreateTableAsync(table);
            }

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync();

            using var command = connection.CreateCommand();

            // Use DuckDB's COPY command to import CSV data efficiently
            // This handles large files better than INSERT statements
            command.CommandText = $"COPY {tableName} FROM '{tempCsvPath.Replace("\\", "\\\\")}' (HEADER, DELIMITER ',', ENCODING 'windows-1252')";

            await command.ExecuteNonQueryAsync();
            await connection.CloseAsync();

            // Clean up temporary file
            if (File.Exists(tempCsvPath))
            {
                File.Delete(tempCsvPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to import CSV data into table '{tableName}': {ex.Message}", ex);
        }
    }

    public async Task<TableDataResult> QueryTableDataAsync(TableDataQuery query)
    {

        var duckdbFilePath = ResolveDbPath(query.DatasetId);
        Console.WriteLine($"------- DuckDB file path: {duckdbFilePath}");
        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");


        // var dataset = DatasetService != null ? await DatasetService.GetDatasetAsync(query.DatasetId) : null;
        // if (dataset == null)
        //     throw new FileNotFoundException("Dataset not found.");

        

        try
        {
            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync();

            // Build the SQL query
            var sqlBuilder = new StringBuilder();

            // SELECT clause. When IncludeRowId is requested, prepend the DuckDB rowid pseudocolumn under a
            // reserved alias so the viewer can target the exact row for edits/deletes; it is stripped from
            // the returned Columns list below so it never renders as a data column.
            var selectParts = new List<string>();
            if (query.IncludeRowId)
                selectParts.Add($"rowid AS {Q(TableDataQuery.RowIdKey)}");
            if (query.SelectedColumns?.Any() == true)
                selectParts.AddRange(query.SelectedColumns.Select(c => $"\"{c}\""));
            else
                selectParts.Add("*");
            sqlBuilder.Append("SELECT " + string.Join(", ", selectParts));

            sqlBuilder.Append($" FROM \"{query.TableName}\"");

            // WHERE clause for filters (reused for the COUNT below so the total matches the filtered view)
            var whereClause = BuildWhereClause(query.Filters);
            sqlBuilder.Append(whereClause);

            // ORDER BY clause
            if (query.SortColumns?.Any() == true)
            {
                var sortClauses = query.SortColumns.Select(s => $"\"{s.ColumnName}\" {(s.IsDescending ? "DESC" : "ASC")}");
                sqlBuilder.Append($" ORDER BY {string.Join(", ", sortClauses)}");
            }
            
            // LIMIT and OFFSET for pagination
            var offset = (query.Page - 1) * query.PageSize;
            sqlBuilder.Append($" LIMIT {query.PageSize} OFFSET {offset}");

            var result = new TableDataResult
            {
                Data = new List<Dictionary<string, object>>(),
                Page = query.Page,
                PageSize = query.PageSize
            };

            using (var command = connection.CreateCommand())
            {
                command.CommandText = sqlBuilder.ToString();

                using var reader = await command.ExecuteReaderAsync();

                // Get column information (the rowid alias is metadata, not a data column).
                var columns = new List<Column>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = reader.GetName(i);
                    if (query.IncludeRowId && columnName == TableDataQuery.RowIdKey)
                        continue;
                    columns.Add(new Column
                    {
                        Name = columnName,
                        DataType = reader.GetFieldType(i).Name
                    });
                }
                result.Columns = columns;

                // Read data rows
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : NormalizeValue(reader.GetValue(i));
                        row[reader.GetName(i)] = value ?? DBNull.Value;
                    }
                    result.Data.Add(row);
                }
            }

            // Total count for pagination — must reflect the full (filtered) result set, NOT just the
            // page returned, or the grid pager can't move past the first page. Run it on the SAME open
            // connection: opening a second connection to the same .duckdb file throws
            // "File is already open" (DuckDB allows one read-write handle per file per process).
            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(*) FROM \"{query.TableName}\"{whereClause}";
                var countScalar = await countCommand.ExecuteScalarAsync();
                result.TotalRows = Convert.ToInt32(countScalar);
            }

            await connection.CloseAsync();

            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to query table data: {ex.Message}", ex);
        }
    }

    // ----- Ad-hoc SQL workbench --------------------------------------------------------------

    // Hard ceiling on rows the workbench will return, regardless of a requested MaxRows — keeps a
    // runaway "SELECT *" from materializing an entire table into memory.
    private const int MaxAdHocRows = 5000;
    private const int QueryTimeoutSeconds = 60;

    private enum SqlKind { Read, Write, Empty }

    public async Task<SqlQueryResult> ExecuteSqlAsync(string datasetId, string sql, bool allowWrite, int maxRows, CancellationToken ct = default)
    {
        var result = new SqlQueryResult();
        var stopwatch = Stopwatch.StartNew();

        var kind = ClassifyStatement(sql);
        if (kind == SqlKind.Empty)
        {
            result.Error = "Query is empty.";
            return result;
        }
        if (kind == SqlKind.Write && !allowWrite)
        {
            result.Error = "This query modifies data and requires edit permission.";
            return result;
        }

        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
        {
            result.Error = "Dataset database not found.";
            return result;
        }

        var cap = maxRows > 0 ? Math.Min(maxRows, MaxAdHocRows) : MaxAdHocRows;
        result.IsSelect = kind == SqlKind.Read;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(QueryTimeoutSeconds));

            // Reads use a read-only handle: multiple readers can coexist (no single-writer lock) and
            // a VIEW_DATA user physically cannot mutate, even via a crafted WITH. Writes need a
            // read-write handle (one writer per file, like imports).
            var connectionString = kind == SqlKind.Read
                ? $"DataSource={duckdbFilePath};ACCESS_MODE=READ_ONLY"
                : $"DataSource={duckdbFilePath}";

            using var connection = new DuckDBConnection(connectionString);
            await connection.OpenAsync(cts.Token);

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = await command.ExecuteReaderAsync(cts.Token);
            if (reader.FieldCount > 0)
                ReadResultSet(reader, result, cap, cts.Token);
            if (kind == SqlKind.Write)
                result.RowsAffected = reader.RecordsAffected;

            await connection.CloseAsync();
        }
        catch (OperationCanceledException)
        {
            result.Error = $"The query was cancelled or exceeded the {QueryTimeoutSeconds}s time limit.";
        }
        catch (Exception ex)
        {
            // Surface the DuckDB error message inline — the workbench shows it to the author.
            result.Error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    public async Task<SqlQueryResult> CreateObjectFromQueryAsync(string datasetId, string objectName, string sql, bool asView, CancellationToken ct = default)
    {
        var result = new SqlQueryResult();
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(objectName) || !Regex.IsMatch(objectName, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            result.Error = "Invalid name. Use letters, digits and underscores; it cannot start with a digit.";
            return result;
        }
        if (ClassifyStatement(sql) != SqlKind.Read)
        {
            result.Error = "Only a single SELECT query can be saved as a table or view.";
            return result;
        }

        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
        {
            result.Error = "Dataset database not found.";
            return result;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(QueryTimeoutSeconds));

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(cts.Token);

            using var command = connection.CreateCommand();
            var objectType = asView ? "VIEW" : "TABLE";
            var inner = sql.TrimEnd().TrimEnd(';'); // compose inside CREATE ... AS (...)
            command.CommandText = $"CREATE OR REPLACE {objectType} \"{objectName}\" AS {inner}";
            result.RowsAffected = await command.ExecuteNonQueryAsync(cts.Token);

            await connection.CloseAsync();
        }
        catch (OperationCanceledException)
        {
            result.Error = $"The operation exceeded the {QueryTimeoutSeconds}s time limit.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    // Re-runs a SELECT against the dataset's own DuckDB tables and refreshes a target table in place
    // (the SqlQuery ingestion kind — e.g. a scheduled Query Notebook cell). Source and target are the
    // same file/connection, so no ATTACH and no cross-database transaction limitation applies here.
    public async Task<ImportResult> RunQueryIntoTableAsync(string datasetId, string tableName, string sql, ImportMode mode, List<string> keyColumns, bool createIfMissing, CancellationToken ct = default)
    {
        var result = new ImportResult();

        if (ClassifyStatement(sql) != SqlKind.Read)
        {
            result.Error = "Only a single SELECT query can be used as an ingestion source.";
            return result;
        }

        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
        {
            result.Error = "Dataset database not found.";
            return result;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(QueryTimeoutSeconds));

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(cts.Token);

            var exists = await TableExistsAsync(connection, tableName, cts.Token);
            if (!exists && !createIfMissing)
            {
                result.Error = $"Target table \"{tableName}\" does not exist.";
                return result;
            }

            var inner = sql.TrimEnd().TrimEnd(';');

            if (!exists || mode == ImportMode.Replace)
            {
                // One statement handles both "create" and "full refresh" — no separate create path.
                await ExecAsync(connection, $"CREATE OR REPLACE TABLE {Q(tableName)} AS {inner}", cts.Token);
                result.RowsInserted = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {Q(tableName)}", cts.Token);
            }
            else if (mode == ImportMode.Upsert)
            {
                if (keyColumns.Count == 0)
                {
                    result.Error = "Upsert requires at least one key column.";
                    return result;
                }

                var keyList = string.Join(", ", keyColumns.Select(Q));
                await ExecAsync(connection, "BEGIN TRANSACTION", cts.Token);
                try
                {
                    var updated = (int)await ScalarLongAsync(connection,
                        $"SELECT COUNT(*) FROM {Q(tableName)} WHERE ({keyList}) IN (SELECT {keyList} FROM ({inner}) AS q)", cts.Token);
                    await ExecAsync(connection, $"DELETE FROM {Q(tableName)} WHERE ({keyList}) IN (SELECT {keyList} FROM ({inner}) AS q)", cts.Token);
                    await ExecAsync(connection, $"INSERT INTO {Q(tableName)} SELECT * FROM ({inner}) AS q", cts.Token);
                    await ExecAsync(connection, "COMMIT", cts.Token);

                    var total = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM ({inner}) AS q", cts.Token);
                    result.RowsUpdated = updated;
                    result.RowsInserted = Math.Max(0, total - updated);
                }
                catch
                {
                    try { await ExecAsync(connection, "ROLLBACK", cts.Token); } catch { /* best-effort */ }
                    throw;
                }
            }
            else // Append
            {
                var before = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {Q(tableName)}", cts.Token);
                await ExecAsync(connection, $"INSERT INTO {Q(tableName)} SELECT * FROM ({inner}) AS q", cts.Token);
                var after = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {Q(tableName)}", cts.Token);
                result.RowsInserted = after - before;
            }

            await connection.CloseAsync();
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Error = $"The query exceeded the {QueryTimeoutSeconds}s time limit.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    // ---- Ingestion: stage → validate → promote (shared by the manual wizard and scheduled pulls) ----

    private const int PreviewRowCap = 50;
    private const string StagingTable = "_ingest_stg";

    public async Task<ImportValidationResult> ValidateImportAsync(string datasetId, string tableName, Stream fileStream, ImportFileFormat format, CancellationToken ct = default)
        => await ValidateInternalAsync(datasetId, fileStream, format, conn => ReadTargetColumnsAsync(conn, tableName, ct), ct);

    public async Task<ImportValidationResult> ValidateImportAgainstSchemaAsync(string datasetId, List<Column> targetColumns, Stream fileStream, ImportFileFormat format, CancellationToken ct = default)
        => await ValidateInternalAsync(datasetId, fileStream, format,
            _ => Task.FromResult(targetColumns.Select(c => (c.Name, c.DataType)).ToList()), ct);

    // Shared validation: stage the file, resolve the target columns (from an existing table or a
    // caller-supplied schema), then report missing/extra columns, per-column TRY_CAST failures and a preview.
    private async Task<ImportValidationResult> ValidateInternalAsync(
        string datasetId, Stream fileStream, ImportFileFormat format,
        Func<DuckDBConnection, Task<List<(string Name, string Type)>>> resolveTargetColumns, CancellationToken ct)
    {
        var result = new ImportValidationResult();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
        {
            result.Error = "Dataset database not found.";
            return result;
        }

        string? tempPath = null;
        try
        {
            tempPath = await WriteTempFileAsync(fileStream, format, ct);

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(ct);

            await StageFileAsync(connection, tempPath, format, ct);

            var stagingColumns = await ReadStagingColumnsAsync(connection, ct);
            var targetColumns = await resolveTargetColumns(connection);

            result.FileColumns = stagingColumns.Select(c => c.Name).ToList();
            var stagingByKey = BuildColumnKeyMap(stagingColumns.Select(c => c.Name));

            // Columns the target expects but the file doesn't supply, and vice-versa.
            result.MissingColumns = targetColumns
                .Where(t => !stagingByKey.ContainsKey(NormalizeColumnKey(t.Name)))
                .Select(t => t.Name).ToList();
            var targetKeys = targetColumns.Select(t => NormalizeColumnKey(t.Name)).ToHashSet();
            result.ExtraColumns = stagingColumns
                .Where(s => !targetKeys.Contains(NormalizeColumnKey(s.Name)))
                .Select(s => s.Name).ToList();

            result.TotalRows = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {StagingTable}", ct);

            // Per common column: how many staged values fail TRY_CAST to the target type.
            foreach (var t in targetColumns)
            {
                if (!stagingByKey.TryGetValue(NormalizeColumnKey(t.Name), out var stgName))
                    continue;

                var invalidWhere = $"{Q(stgName)} IS NOT NULL AND TRY_CAST({Q(stgName)} AS {t.Type}) IS NULL";
                var invalidCount = (int)await ScalarLongAsync(connection,
                    $"SELECT COUNT(*) FROM {StagingTable} WHERE {invalidWhere}", ct);
                if (invalidCount == 0)
                    continue;

                var samples = new List<string>();
                using (var sCmd = connection.CreateCommand())
                {
                    sCmd.CommandText = $"SELECT DISTINCT {Q(stgName)} FROM {StagingTable} WHERE {invalidWhere} LIMIT 5";
                    using var sReader = await sCmd.ExecuteReaderAsync(ct);
                    while (await sReader.ReadAsync(ct))
                        samples.Add(sReader.IsDBNull(0) ? "" : sReader.GetValue(0)?.ToString() ?? "");
                }

                result.ColumnValidations.Add(new ColumnValidation
                {
                    Column = t.Name,
                    TargetType = t.Type,
                    InvalidCount = invalidCount,
                    SampleInvalidValues = samples
                });
            }

            // Preview the first N staged rows.
            using (var pCmd = connection.CreateCommand())
            {
                pCmd.CommandText = $"SELECT * FROM {StagingTable} LIMIT {PreviewRowCap}";
                using var pReader = await pCmd.ExecuteReaderAsync(ct);
                while (await pReader.ReadAsync(ct))
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < pReader.FieldCount; i++)
                        row[pReader.GetName(i)] = pReader.IsDBNull(i) ? null : NormalizeValue(pReader.GetValue(i));
                    result.PreviewRows.Add(row);
                }
            }

            await connection.CloseAsync();
        }
        catch (OperationCanceledException)
        {
            result.Error = "Validation was cancelled or timed out.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            TryDelete(tempPath);
        }

        return result;
    }

    public async Task<FilePeekResult> PeekFileAsync(string datasetId, Stream fileStream, ImportFileFormat format, CancellationToken ct = default)
    {
        var result = new FilePeekResult();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
        {
            result.Error = "Dataset database not found.";
            return result;
        }

        string? tempPath = null;
        try
        {
            tempPath = await WriteTempFileAsync(fileStream, format, ct);

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(ct);

            await StageFileAsync(connection, tempPath, format, ct);

            var stagingColumns = await ReadStagingColumnsAsync(connection, ct);
            result.Columns = stagingColumns
                .Select(c => new Column { Name = c.Name, DataType = MapToCommonType(c.Type), IsNullable = true })
                .ToList();
            result.TotalRows = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {StagingTable}", ct);

            using (var pCmd = connection.CreateCommand())
            {
                pCmd.CommandText = $"SELECT * FROM {StagingTable} LIMIT {PreviewRowCap}";
                using var pReader = await pCmd.ExecuteReaderAsync(ct);
                while (await pReader.ReadAsync(ct))
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < pReader.FieldCount; i++)
                        row[pReader.GetName(i)] = pReader.IsDBNull(i) ? null : NormalizeValue(pReader.GetValue(i));
                    result.PreviewRows.Add(row);
                }
            }

            await connection.CloseAsync();
        }
        catch (OperationCanceledException)
        {
            result.Error = "File peek was cancelled or timed out.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            TryDelete(tempPath);
        }

        return result;
    }

    // Maps a DuckDB column type to the closest entry in Column.CommonDataTypes (for the schema editor).
    private static string MapToCommonType(string duckType)
    {
        var t = duckType.ToUpperInvariant();
        if (t.StartsWith("DECIMAL") || t.StartsWith("NUMERIC")) return "DECIMAL";
        if (t is "INTEGER" or "INT" or "INT4" or "SIGNED" or "SMALLINT" or "TINYINT") return "INTEGER";
        if (t is "BIGINT" or "INT8" or "LONG" or "HUGEINT" or "UBIGINT") return "BIGINT";
        if (t is "DOUBLE" or "FLOAT" or "FLOAT8" or "FLOAT4" or "REAL") return "DOUBLE";
        if (t is "BOOLEAN" or "BOOL") return "BOOLEAN";
        if (t.StartsWith("TIMESTAMP")) return "TIMESTAMP";
        if (t == "DATE") return "DATE";
        if (t.StartsWith("TIME")) return "TIME";
        if (t == "UUID") return "UUID";
        if (t == "JSON") return "JSON";
        if (t is "BLOB" or "BYTEA") return "BLOB";
        return "VARCHAR";
    }

    public async Task<ImportResult> ImportFileAsync(string datasetId, string tableName, Stream fileStream, ImportFileFormat format, ImportMode mode, List<string> keyColumns, bool skipInvalidRows, bool createIfMissing = false, CancellationToken ct = default)
    {
        var result = new ImportResult();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
        {
            // Include the resolved path: an empty/leading-slash path means the process's Duckdb:DuckdbFilePath
            // config is missing (a common web-app vs scheduler mismatch).
            result.Error = $"Dataset database not found at '{duckdbFilePath}'.";
            return result;
        }

        string? tempPath = null;
        try
        {
            tempPath = await WriteTempFileAsync(fileStream, format, ct);

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(ct);

            // If the target table doesn't exist, either create it from the file's inferred schema
            // (create-and-load in one shot) or fail with a clear message.
            if (!await TableExistsAsync(connection, tableName, ct))
            {
                if (!createIfMissing)
                {
                    result.Error = $"Target table \"{tableName}\" does not exist. Enable \"create target table if it doesn't exist\" to create it automatically.";
                    return result;
                }

                if (format == ImportFileFormat.Excel)
                    await EnsureExcelExtensionAsync(connection, ct);

                await ExecAsync(connection, $"CREATE TABLE {Q(tableName)} AS SELECT * FROM {InferringReaderExpr(format, tempPath)}", ct);
                result.RowsInserted = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {Q(tableName)}", ct);
                result.Success = true;
                await connection.CloseAsync();
                return result;
            }

            await StageFileAsync(connection, tempPath, format, ct);

            var stagingColumns = await ReadStagingColumnsAsync(connection, ct);
            var targetColumns = await ReadTargetColumnsAsync(connection, tableName, ct);
            var stagingByKey = BuildColumnKeyMap(stagingColumns.Select(c => c.Name));

            // Only columns present in BOTH the file and the target are written. Matched on a normalized
            // key so a raw file header (e.g. "Mrp (LBP)") pairs with the slugged target column it created
            // ("mrp__lbp_"); see NormalizeColumnKey.
            var common = targetColumns
                .Where(t => stagingByKey.ContainsKey(NormalizeColumnKey(t.Name)))
                .Select(t => (Target: t.Name, Type: t.Type, Staging: stagingByKey[NormalizeColumnKey(t.Name)]))
                .ToList();

            if (common.Count == 0)
            {
                result.Error = "None of the file's columns match the target table.";
                return result;
            }

            // skipInvalidRows → TRY_CAST + a per-column validity filter (bad rows are dropped, counted).
            // Otherwise → strict CAST, so a bad value aborts the whole import (the file is rejected).
            var castFn = skipInvalidRows ? "TRY_CAST" : "CAST";
            var insertList = string.Join(", ", common.Select(c => Q(c.Target)));
            var selectList = string.Join(", ", common.Select(c => $"{castFn}({Q(c.Staging)} AS {c.Type})"));

            string validFilter = string.Empty;
            if (skipInvalidRows)
            {
                var conds = common.Select(c => $"({Q(c.Staging)} IS NULL OR TRY_CAST({Q(c.Staging)} AS {c.Type}) IS NOT NULL)");
                validFilter = " WHERE " + string.Join(" AND ", conds);
            }

            var stagedCount = (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {StagingTable}", ct);
            var validCount = skipInvalidRows
                ? (int)await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {StagingTable}{validFilter}", ct)
                : stagedCount;
            result.RowsSkipped = stagedCount - validCount;

            var insertSql = $"INSERT INTO {Q(tableName)} ({insertList}) SELECT {selectList} FROM {StagingTable}{validFilter}";

            // Wrap the mutations in a transaction so a failed INSERT (e.g. strict CAST on a bad value)
            // rolls back any preceding DELETE — Replace/Upsert must never leave the table half-emptied.
            await ExecAsync(connection, "BEGIN TRANSACTION", ct);
            try
            {
                if (mode == ImportMode.Replace)
                {
                    await ExecAsync(connection, $"DELETE FROM {Q(tableName)}", ct);
                    await ExecAsync(connection, insertSql, ct);
                    result.RowsInserted = validCount;
                }
                else if (mode == ImportMode.Upsert)
                {
                    var keys = ResolveKeyColumns(keyColumns, common);
                    if (keys.Count == 0)
                    {
                        await ExecAsync(connection, "ROLLBACK", ct);
                        result.Error = "Upsert requires at least one key column that exists in both the file and the table.";
                        return result;
                    }

                    var targetTuple = string.Join(", ", keys.Select(k => Q(k.Target)));
                    var stagingKeySelect = string.Join(", ", keys.Select(k => $"TRY_CAST({Q(k.Staging)} AS {k.Type})"));
                    var keySubquery = $"SELECT {stagingKeySelect} FROM {StagingTable}{validFilter}";

                    // Rows in the target that the file replaces (matched on the key tuple) = "updated".
                    var updated = (int)await ScalarLongAsync(connection,
                        $"SELECT COUNT(*) FROM {Q(tableName)} WHERE ({targetTuple}) IN ({keySubquery})", ct);

                    await ExecAsync(connection, $"DELETE FROM {Q(tableName)} WHERE ({targetTuple}) IN ({keySubquery})", ct);
                    await ExecAsync(connection, insertSql, ct);

                    result.RowsUpdated = updated;
                    result.RowsInserted = Math.Max(0, validCount - updated);
                }
                else // Append
                {
                    await ExecAsync(connection, insertSql, ct);
                    result.RowsInserted = validCount;
                }

                await ExecAsync(connection, "COMMIT", ct);
            }
            catch
            {
                try { await ExecAsync(connection, "ROLLBACK", ct); } catch { /* best-effort */ }
                throw;
            }

            await connection.CloseAsync();
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Import was cancelled or timed out.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            TryDelete(tempPath);
        }

        return result;
    }

    // Resolves the requested upsert key columns (given as target names) to common columns.
    private static List<(string Target, string Type, string Staging)> ResolveKeyColumns(
        List<string> keyColumns, List<(string Target, string Type, string Staging)> common)
    {
        if (keyColumns == null) return new();
        var byLower = common.ToDictionary(c => c.Target.ToLowerInvariant(), c => c);
        return keyColumns
            .Select(k => k?.Trim().ToLowerInvariant() ?? "")
            .Where(k => k.Length > 0 && byLower.ContainsKey(k))
            .Select(k => byLower[k])
            .ToList();
    }

    // Writes the uploaded stream to a temp file. Text formats are re-encoded windows-1252 → UTF-8
    // (matching the legacy CSV import); binary/structured formats are copied byte-for-byte.
    private static async Task<string> WriteTempFileAsync(Stream source, ImportFileFormat format, CancellationToken ct)
    {
        var ext = format switch
        {
            ImportFileFormat.Tsv => ".tsv",
            ImportFileFormat.Json => ".json",
            ImportFileFormat.Parquet => ".parquet",
            ImportFileFormat.Excel => ".xlsx",
            _ => ".csv"
        };
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{ext}");

        if (format == ImportFileFormat.Csv || format == ImportFileFormat.Tsv)
        {
            using var reader = new StreamReader(source, Encoding.GetEncoding("windows-1252"));
            using var writer = new StreamWriter(tempPath, false, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                await writer.WriteLineAsync(line);
        }
        else
        {
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            await source.CopyToAsync(fileStream, ct);
        }

        return tempPath;
    }

    // Like ReaderExpr but lets DuckDB infer column types (no all_varchar) — used when creating a brand-new
    // target table so it gets sensible typed columns instead of all VARCHAR.
    private static string InferringReaderExpr(ImportFileFormat format, string tempPath)
    {
        var p = tempPath.Replace("\\", "\\\\").Replace("'", "''");
        return format switch
        {
            ImportFileFormat.Tsv => $"read_csv('{p}', delim='\\t', header=true)",
            ImportFileFormat.Json => $"read_json_auto('{p}')",
            ImportFileFormat.Parquet => $"read_parquet('{p}')",
            ImportFileFormat.Excel => $"read_xlsx('{p}', header=true)",
            _ => $"read_csv_auto('{p}', header=true, sample_size=-1)"
        };
    }

    private static async Task<bool> TableExistsAsync(DuckDBConnection connection, string tableName, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{tableName.Replace("'", "''")}'";
        var value = await cmd.ExecuteScalarAsync(ct);
        var normalized = NormalizeValue(value);
        return normalized != null && Convert.ToInt64(normalized) > 0;
    }

    // The DuckDB reader expression for a given format + temp file path.
    private static string ReaderExpr(ImportFileFormat format, string tempPath)
    {
        var p = tempPath.Replace("\\", "\\\\").Replace("'", "''");
        return format switch
        {
            ImportFileFormat.Tsv => $"read_csv('{p}', delim='\\t', header=true, all_varchar=true)",
            ImportFileFormat.Json => $"read_json_auto('{p}')",
            ImportFileFormat.Parquet => $"read_parquet('{p}')",
            ImportFileFormat.Excel => $"read_xlsx('{p}', header=true)",
            _ => $"read_csv_auto('{p}', header=true, sample_size=-1, all_varchar=true)"
        };
    }

    // Materializes the file into a session-scoped TEMP table (auto-dropped on connection close).
    private async Task StageFileAsync(DuckDBConnection connection, string tempPath, ImportFileFormat format, CancellationToken ct)
    {
        if (format == ImportFileFormat.Excel)
            await EnsureExcelExtensionAsync(connection, ct);

        await ExecAsync(connection, $"CREATE OR REPLACE TEMP TABLE {StagingTable} AS SELECT * FROM {ReaderExpr(format, tempPath)}", ct);
    }

    // Installs + loads the DuckDB `excel` extension, after pinning the extension directory to a path we
    // control. Without this, DuckDB derives the extension dir from the OS home directory; under a Windows
    // service / IIS app pool identity that home is C:\Windows\System32\config\systemprofile (locked down),
    // so INSTALL excel throws "Can't find the home directory...". Setting extension_directory explicitly
    // bypasses that lookup. The directory is created on disk first; the service account must be able to
    // write to it, and reach extensions.duckdb.org once to download the extension (it is cached after).
    private async Task EnsureExcelExtensionAsync(DuckDBConnection connection, CancellationToken ct)
    {
        var extDir = _option.ResolveExtensionDirectory();
        Directory.CreateDirectory(extDir);
        await ExecAsync(connection, $"SET extension_directory = '{extDir.Replace("\\", "\\\\").Replace("'", "''")}';", ct);
        await ExecAsync(connection, "INSTALL excel; LOAD excel;", ct);
    }

    private static async Task<List<(string Name, string Type)>> ReadStagingColumnsAsync(DuckDBConnection connection, CancellationToken ct)
    {
        var cols = new List<(string, string)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DESCRIBE {StagingTable}"; // column_name, column_type, ...
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            cols.Add((reader.GetString(0), reader.GetString(1)));
        return cols;
    }

    private static async Task<List<(string Name, string Type)>> ReadTargetColumnsAsync(DuckDBConnection connection, string tableName, CancellationToken ct)
    {
        var cols = new List<(string, string)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''")}')"; // cid, name, type, notnull, dflt, pk
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            cols.Add((reader.GetString(1), reader.GetString(2)));
        return cols;
    }

    private static async Task ExecAsync(DuckDBConnection connection, string sql, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarLongAsync(DuckDBConnection connection, string sql, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(ct);
        var normalized = NormalizeValue(value);
        return normalized is null ? 0 : Convert.ToInt64(normalized);
    }

    // Quotes a SQL identifier (table/column) for safe embedding.
    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    // Normalizes a column name to the key used to pair file (staging) columns with target table columns.
    // Mirrors the client-side SanitizeColumnName applied when a table is created from a file
    // (SchemaConfigurationStep.SanitizeColumnName): lowercase + every char that isn't a letter, digit or
    // underscore becomes '_'. This lets a raw file header like "Mrp (LBP)" match the stored column
    // "mrp__lbp_", and "Sr.No" match "sr_no". Idempotent on already-sanitized names.
    private static string NormalizeColumnKey(string name) =>
        Regex.Replace(name ?? string.Empty, "[^a-zA-Z0-9_]", "_").ToLowerInvariant();

    // Maps each column's normalized key → its actual name. First occurrence wins, so two file columns
    // that collapse to the same key (rare) don't throw; the duplicate is simply treated as unmatched.
    private static Dictionary<string, string> BuildColumnKeyMap(IEnumerable<string> names)
    {
        var map = new Dictionary<string, string>();
        foreach (var name in names)
            map.TryAdd(NormalizeColumnKey(name), name);
        return map;
    }

    private static void TryDelete(string? path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); }
        catch { /* best-effort temp cleanup */ }
    }

    private static void ReadResultSet(DbDataReader reader, SqlQueryResult result, int cap, CancellationToken ct)
    {
        var columns = new List<Column>();
        for (int i = 0; i < reader.FieldCount; i++)
            columns.Add(new Column { Name = reader.GetName(i), DataType = reader.GetFieldType(i).Name });
        result.Columns = columns;

        int count = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (count >= cap)
            {
                // There was at least one more row than the cap — flag it and stop.
                result.Truncated = true;
                break;
            }
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : NormalizeValue(reader.GetValue(i));
            result.Rows.Add(row);
            count++;
        }
        result.RowsReturned = result.Rows.Count;
    }

    // Some DuckDB types don't serialize cleanly to JSON as a plain value. The main offender is
    // HUGEINT (e.g. the result of SUM over an integer column), which DuckDB.NET surfaces as a
    // System.Numerics.BigInteger — System.Text.Json has no built-in support for it and would emit
    // {"isPowerOfTwo":...,"sign":...} instead of a number. Coerce it to a long (the common case),
    // falling back to its string form only when the value exceeds long's range.
    private static object? NormalizeValue(object? value)
    {
        if (value is null || value is DBNull) return null;

        if (value is System.Numerics.BigInteger big)
        {
            if (big >= long.MinValue && big <= long.MaxValue) return (long)big;
            return big.ToString();
        }

        return value;
    }

    // Routes a statement to the read or write path. Comment-stripping + first-keyword inspection is
    // enough to *route* (choose connection mode + required role); the read-only connection is the
    // hard guarantee that a misclassified write can't actually mutate.
    private static SqlKind ClassifyStatement(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return SqlKind.Empty;

        var cleaned = StripSqlComments(sql).Trim();
        if (cleaned.Length == 0) return SqlKind.Empty;

        // Keep the read path to a single statement; multi-statement input is treated as a write
        // (so it needs edit permission) and the read-only handle still blocks any mutation.
        var statements = cleaned.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (statements.Count == 0) return SqlKind.Empty;
        if (statements.Count > 1) return SqlKind.Write;

        var firstWord = new string(statements[0].TrimStart().TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return firstWord is "SELECT" or "WITH" or "TABLE" or "FROM" or "VALUES" or "DESCRIBE" or "SHOW" or "PRAGMA" or "EXPLAIN" or "SUMMARIZE"
            ? SqlKind.Read
            : SqlKind.Write;
    }

    private static string StripSqlComments(string sql)
    {
        // Best-effort: drop /* block */ then -- line comments. Only used for routing, never executed.
        var noBlock = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"--[^\n]*", " ");
    }

    // Builds the " WHERE ..." fragment (leading space included) from filters, or "" when there are none.
    private string BuildWhereClause(List<FilterCondition>? filters)
    {
        if (filters?.Any() != true)
            return string.Empty;

        var filterClauses = new List<string>();
        foreach (var filter in filters)
        {
            var filterClause = BuildFilterClause(filter);
            if (!string.IsNullOrEmpty(filterClause))
                filterClauses.Add(filterClause);
        }

        return filterClauses.Any() ? $" WHERE {string.Join(" AND ", filterClauses)}" : string.Empty;
    }

    public async Task<int> GetTableRowCountAsync(string datasetId, string tableName, List<FilterCondition>? filters = null)
    {
        // var dataset = DatasetService != null ? await DatasetService.GetDatasetAsync(datasetId) : null;
        // if (dataset == null)
        //     throw new FileNotFoundException("Dataset not found.");

        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath))
            throw new FileNotFoundException($"Database not found at '{duckdbFilePath}'.");

        try
        {
            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync();

            var sqlBuilder = new StringBuilder($"SELECT COUNT(*) FROM \"{tableName}\"");
            
            // WHERE clause for filters
            if (filters?.Any() == true)
            {
                var filterClauses = new List<string>();
                foreach (var filter in filters)
                {
                    var filterClause = BuildFilterClause(filter);
                    if (!string.IsNullOrEmpty(filterClause))
                    {
                        filterClauses.Add(filterClause);
                    }
                }
                
                if (filterClauses.Any())
                {
                    sqlBuilder.Append($" WHERE {string.Join(" AND ", filterClauses)}");
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = sqlBuilder.ToString();
            
            var result = await command.ExecuteScalarAsync();
            await connection.CloseAsync();
            
            return Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            
            throw new InvalidOperationException($"Failed to get table row count: {ex.Message}", ex);
        }
    }

    // ----- Row-level editing (data viewer) --------------------------------------------------------
    // Rows are addressed by the DuckDB rowid pseudocolumn, which is unique per row in a base table — so
    // edits/deletes target exactly one row even when the table has no primary key. Values arrive as
    // strings and are CAST to the column's declared type, so an empty value in a non-text column becomes
    // NULL rather than a cast error.

    public async Task<RowMutationResult> UpdateRowAsync(string datasetId, string tableName, long rowId, Dictionary<string, string?> values, CancellationToken ct = default)
    {
        var result = new RowMutationResult();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath)) { result.Error = "Dataset database not found."; return result; }
        if (values == null || values.Count == 0) { result.Error = "No values to update."; return result; }

        try
        {
            var columnsByName = await GetColumnLookupAsync(datasetId, tableName);

            var assignments = new List<string>();
            foreach (var kv in values)
            {
                // Ignore keys that aren't real columns (e.g. the reserved rowid alias).
                if (!columnsByName.TryGetValue(kv.Key, out var col)) continue;
                assignments.Add($"{Q(col.Name)} = {ToSqlLiteral(kv.Value, col.DataType)}");
            }
            if (assignments.Count == 0) { result.Error = "No valid columns to update."; return result; }

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {Q(tableName)} SET {string.Join(", ", assignments)} WHERE rowid = {rowId}";
            result.RowsAffected = await command.ExecuteNonQueryAsync(ct);
            await connection.CloseAsync();

            result.Success = result.RowsAffected > 0;
            if (!result.Success)
                result.Error = "Row not found — it may have changed or been removed. Refresh and try again.";
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }

    public async Task<RowMutationResult> InsertRowAsync(string datasetId, string tableName, Dictionary<string, string?> values, CancellationToken ct = default)
    {
        var result = new RowMutationResult();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath)) { result.Error = "Dataset database not found."; return result; }

        try
        {
            var columnsByName = await GetColumnLookupAsync(datasetId, tableName);

            var columnList = new List<string>();
            var valueList = new List<string>();
            foreach (var kv in values ?? new())
            {
                if (!columnsByName.TryGetValue(kv.Key, out var col)) continue;
                columnList.Add(Q(col.Name));
                valueList.Add(ToSqlLiteral(kv.Value, col.DataType));
            }
            if (columnList.Count == 0) { result.Error = "No valid columns provided."; return result; }

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO {Q(tableName)} ({string.Join(", ", columnList)}) VALUES ({string.Join(", ", valueList)})";
            result.RowsAffected = await command.ExecuteNonQueryAsync(ct);
            await connection.CloseAsync();

            result.Success = result.RowsAffected > 0;
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }

    public async Task<RowMutationResult> DeleteRowAsync(string datasetId, string tableName, long rowId, CancellationToken ct = default)
    {
        var result = new RowMutationResult();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath)) { result.Error = "Dataset database not found."; return result; }

        try
        {
            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {Q(tableName)} WHERE rowid = {rowId}";
            result.RowsAffected = await command.ExecuteNonQueryAsync(ct);
            await connection.CloseAsync();

            result.Success = result.RowsAffected > 0;
            if (!result.Success)
                result.Error = "Row not found — it may have already been removed.";
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }

    public async Task<BulkRowEditResult> ApplyRowChangesAsync(string datasetId, string tableName, BulkRowEditRequest changes, CancellationToken ct = default)
    {
        var result = new BulkRowEditResult();
        var duckdbFilePath = ResolveDbPath(datasetId);
        if (!File.Exists(duckdbFilePath)) { result.Error = "Dataset database not found."; return result; }
        if (changes == null) { result.Error = "No changes provided."; return result; }

        try
        {
            var columnsByName = await GetColumnLookupAsync(datasetId, tableName);

            using var connection = new DuckDBConnection($"DataSource={duckdbFilePath}");
            await connection.OpenAsync(ct);

            // One transaction for the whole batch: if any statement fails the table is left untouched.
            // Order matters — updates run against the rowids captured at load time before any deletes
            // remove rows; inserts come last.
            await ExecAsync(connection, "BEGIN TRANSACTION", ct);
            try
            {
                foreach (var update in changes.Updates)
                {
                    if (update?.RowId == null || update.Values == null) continue;
                    var assignments = new List<string>();
                    foreach (var kv in update.Values)
                    {
                        if (!columnsByName.TryGetValue(kv.Key, out var col)) continue;
                        assignments.Add($"{Q(col.Name)} = {ToSqlLiteral(kv.Value, col.DataType)}");
                    }
                    if (assignments.Count == 0) continue;

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"UPDATE {Q(tableName)} SET {string.Join(", ", assignments)} WHERE rowid = {update.RowId.Value}";
                    result.Updated += await cmd.ExecuteNonQueryAsync(ct);
                }

                foreach (var rowId in changes.Deletes.Distinct())
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {Q(tableName)} WHERE rowid = {rowId}";
                    result.Deleted += await cmd.ExecuteNonQueryAsync(ct);
                }

                foreach (var insert in changes.Inserts)
                {
                    if (insert?.Values == null) continue;
                    var columnList = new List<string>();
                    var valueList = new List<string>();
                    foreach (var kv in insert.Values)
                    {
                        if (!columnsByName.TryGetValue(kv.Key, out var col)) continue;
                        columnList.Add(Q(col.Name));
                        valueList.Add(ToSqlLiteral(kv.Value, col.DataType));
                    }
                    if (columnList.Count == 0) continue;

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"INSERT INTO {Q(tableName)} ({string.Join(", ", columnList)}) VALUES ({string.Join(", ", valueList)})";
                    result.Inserted += await cmd.ExecuteNonQueryAsync(ct);
                }

                await ExecAsync(connection, "COMMIT", ct);
                result.Success = true;
            }
            catch
            {
                try { await ExecAsync(connection, "ROLLBACK", ct); } catch { /* best-effort */ }
                throw;
            }

            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    // Case-insensitive map of column name → column (with declared type) for the table.
    private async Task<Dictionary<string, Column>> GetColumnLookupAsync(string datasetId, string tableName)
    {
        var columns = await GetTableColumnsAsync(datasetId, tableName);
        var map = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
            map.TryAdd(col.Name, col);
        return map;
    }

    // Renders a single edited value as a SQL literal for the given column type. null (and empty values in
    // a non-text column) become NULL; everything else is a single-quote-escaped string CAST to the column
    // type, which lets DuckDB parse numbers/dates/booleans from their text form. The data type comes from
    // PRAGMA table_info (trusted), so interpolating it is safe.
    private static string ToSqlLiteral(string? value, string dataType)
    {
        if (value == null) return "NULL";

        var type = (dataType ?? string.Empty).ToUpperInvariant();
        var isTextual = type.Contains("CHAR") || type.Contains("TEXT") || type == "STRING"
            || type == "JSON" || type == "UUID" || type.Contains("BLOB");
        if (value.Length == 0 && !isTextual) return "NULL";

        var escaped = value.Replace("'", "''");
        return string.IsNullOrWhiteSpace(dataType)
            ? $"'{escaped}'"
            : $"CAST('{escaped}' AS {dataType})";
    }

    //private async Task<string> CleanCsvStreamToTempFileAsync(Stream csvStream, Encoding sourceEncoding)
    //{
    //    var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.csv");

    //    using (var reader = new StreamReader(csvStream, sourceEncoding))
    //    using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
    //    {
    //        while (!reader.EndOfStream)
    //        {
    //            var line = await reader.ReadLineAsync();
    //            if (line != null)
    //            {
    //                // Replace in the text
    //                line = line.Replace("&", "AND");
    //                await writer.WriteLineAsync(line);
    //            }
    //        }
    //    }

    //    return tempPath;
    //}


    private string BuildFilterClause(FilterCondition filter)
    {
        if (string.IsNullOrWhiteSpace(filter.ColumnName) || string.IsNullOrWhiteSpace(filter.Value))
            return string.Empty;

        var columnName = $"\"{filter.ColumnName}\"";
        var value = filter.Value.Replace("'", "''"); // Escape single quotes

        return filter.Operator.ToLower() switch
        {
            "equals" => $"{columnName} = '{value}'",
            "contains" => $"{columnName} LIKE '%{value}%'",
            "startswith" => $"{columnName} LIKE '{value}%'",
            "endswith" => $"{columnName} LIKE '%{value}'",
            "greaterthan" => $"{columnName} > '{value}'",
            "lessthan" => $"{columnName} < '{value}'",
            _ => $"{columnName} = '{value}'"
        };
    }
}

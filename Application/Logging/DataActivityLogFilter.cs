using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Application.Shared.Models.Logging;
using Application.Shared.Services.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Application.Logging;

/// <summary>
/// Logs one <c>data_app_log</c> row per API call to the Datasets / Tables / Ingestion / Sharing
/// controllers: who did what, against which dataset/table, the requested query/filters, the outcome
/// and how long it took. Registered globally but only acts on the target controllers.
/// </summary>
public class DataActivityLogFilter : IAsyncActionFilter
{
    private readonly IDataAppLogService _log;

    public DataActivityLogFilter(IDataAppLogService log) => _log = log;

    private static readonly HashSet<string> TargetControllers =
        new(StringComparer.OrdinalIgnoreCase) { "Datasets", "Ingestion", "Sharing", "Query" };

    // Action method name -> (area, action). Anything unmapped falls back to a derived name.
    private static readonly Dictionary<string, (string Area, string Action)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Datasets
            ["GetDatasets"] = ("dataset", "dataset.list"),
            ["GetDataset"] = ("dataset", "dataset.open"),
            ["CreateDataset"] = ("dataset", "dataset.create"),
            ["UpdateDataset"] = ("dataset", "dataset.update"),
            ["DeleteDataset"] = ("dataset", "dataset.delete"),
            ["GetDatasetsStats"] = ("dataset", "dataset.stats"),
            ["DatabaseExists"] = ("dataset", "dataset.database_exists"),
            ["CreateDatabase"] = ("dataset", "dataset.create_database"),
            ["GetSourceDatabases"] = ("dataset", "dataset.source_databases"),
            ["GetSourceTables"] = ("dataset", "dataset.source_tables"),
            // Tables
            ["InferSchema"] = ("table", "table.infer_schema"),
            ["CreateTable"] = ("table", "table.create"),
            ["GetTables"] = ("table", "table.list"),
            ["GetTableStats"] = ("table", "table.stats"),
            ["GetTable"] = ("table", "table.open"),
            ["GetColumns"] = ("table", "table.columns"),
            ["DeleteTable"] = ("table", "table.delete"),
            ["DownloadTable"] = ("table", "table.download"),
            ["DownloadFilteredTable"] = ("table", "table.download_filtered"),
            ["ImportCsvData"] = ("table", "table.import_csv"),
            ["ImportExternalCsvData"] = ("table", "table.import_csv_external"),
            ["PeekFile"] = ("table", "table.peek_file"),
            ["ValidateImport"] = ("table", "table.validate_import"),
            ["ValidateSchema"] = ("table", "table.validate_schema"),
            ["ImportFile"] = ("table", "table.import"),
            ["GetTableData"] = ("table", "table.query"),
            ["GetTableRowCount"] = ("table", "table.count"),
            ["UpdateRow"] = ("table", "row.update"),
            ["InsertRow"] = ("table", "row.add"),
            ["DeleteRow"] = ("table", "row.delete"),
            ["BulkEditRows"] = ("table", "row.bulk_edit"),
            // Ingestion
            ["GetSources"] = ("ingestion", "ingestion.list"),
            ["GetSource"] = ("ingestion", "ingestion.open"),
            ["CreateSource"] = ("ingestion", "ingestion.create"),
            ["UpdateSource"] = ("ingestion", "ingestion.update"),
            ["DeleteSource"] = ("ingestion", "ingestion.delete"),
            ["GetRuns"] = ("ingestion", "ingestion.runs"),
            ["RunNow"] = ("ingestion", "ingestion.run"),
            ["RunBatch"] = ("ingestion", "ingestion.run_batch"),
            ["ReconcileRuns"] = ("ingestion", "ingestion.reconcile"),
            // Sharing
            ["GetDatasetUsers"] = ("sharing", "sharing.list"),
            ["ShareDataset"] = ("sharing", "sharing.share_dataset"),
            ["GrantTable"] = ("sharing", "sharing.grant_table"),
            ["UpdateUserAccess"] = ("sharing", "sharing.update_access"),
            ["RevokeTableAccess"] = ("sharing", "sharing.revoke_table"),
            ["RemoveUserAccess"] = ("sharing", "sharing.remove_user"),
            // Query workbench
            ["Run"] = ("table", "table.query_run"),
            ["SaveResult"] = ("table", "table.query_save_result"),
            ["GetSavedQueries"] = ("table", "query.saved_list"),
            ["CreateSavedQuery"] = ("table", "query.saved_create"),
            ["UpdateSavedQuery"] = ("table", "query.saved_update"),
            ["DeleteSavedQuery"] = ("table", "query.saved_delete"),
        };

    private static readonly HashSet<string> QueryActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "table.query", "table.count", "table.download_filtered"
        };

    // Property names on a request body that carry the user's SQL / query text.
    private static readonly string[] SqlProps = { "Sql", "Query", "QueryText" };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
        var controller = descriptor?.ControllerName;

        // Fast path: not a target controller, or logging off — do nothing.
        if (!_log.Enabled || controller is null || !TargetControllers.Contains(controller))
        {
            await next();
            return;
        }

        var actionName = descriptor!.ActionName;
        var sw = Stopwatch.StartNew();

        // Snapshot request info before executing (arguments may be consumed by the action).
        var entry = BuildEntry(context, controller, actionName);

        ActionExecutedContext executed;
        try
        {
            executed = await next();
        }
        catch (Exception ex)
        {
            sw.Stop();
            entry.DurationMs = sw.ElapsedMilliseconds;
            entry.Success = false;
            entry.StatusCode = StatusCodes.Status500InternalServerError;
            entry.Error = Truncate(ex.Message, 2000);
            SafeEnqueue(entry);
            throw;
        }

        sw.Stop();
        entry.DurationMs = sw.ElapsedMilliseconds;
        entry.StatusCode = context.HttpContext.Response?.StatusCode ?? 0;
        if (executed.Exception is { } actionEx)
        {
            entry.Success = false;
            entry.Error = Truncate(actionEx.Message, 2000);
            if (entry.StatusCode is 0 or 200) entry.StatusCode = StatusCodes.Status500InternalServerError;
        }
        else
        {
            entry.Success = entry.StatusCode is 0 or (>= 200 and < 400);
        }

        SafeEnqueue(entry);
    }

    private DataAppLogEntry BuildEntry(ActionExecutingContext context, string controller, string actionName)
    {
        var http = context.HttpContext;
        var req = http.Request;

        var (area, action) = Map.TryGetValue(actionName, out var m)
            ? m
            : (controller.ToLowerInvariant(), $"{controller.ToLowerInvariant()}.{ToSnake(actionName)}");

        var route = req.Path.HasValue ? req.Path.Value! : $"{controller}/{actionName}";
        var routeValues = context.RouteData.Values;
        var args = context.ActionArguments;

        var datasetId = FirstNonEmpty(
            Str(routeValues.GetValueOrDefault("datasetId")),
            area == "dataset" ? Str(routeValues.GetValueOrDefault("id")) : null,
            Str(Arg(args, "datasetId")));

        var tableName = FirstNonEmpty(
            Str(routeValues.GetValueOrDefault("tableName")),
            Str(Arg(args, "tableName")));

        var details = SerializeArguments(args);
        var queryText = FirstNonEmpty(
            Arg(args, "sql") as string,
            ExtractSqlFromBody(args),
            QueryActions.Contains(action) ? details : null) ?? string.Empty;

        return new DataAppLogEntry
        {
            EventTime = DateTime.UtcNow,
            CompanyId = req.Headers["X-Company-ID"].FirstOrDefault() ?? string.Empty,
            UserId = FirstNonEmpty(
                req.Headers["UserId"].FirstOrDefault(),
                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value) ?? string.Empty,
            UserName = FirstNonEmpty(
                http.User.Identity?.Name,
                http.User.FindFirst("preferred_username")?.Value,
                http.User.FindFirst("name")?.Value) ?? string.Empty,
            Source = "api",
            Area = area,
            Action = action,
            DatasetId = datasetId ?? string.Empty,
            TableName = tableName ?? string.Empty,
            HttpMethod = req.Method,
            Route = route,
            QueryString = req.QueryString.HasValue ? req.QueryString.Value! : string.Empty,
            QueryText = Truncate(queryText, 16000),
            Details = details,
            ClientIp = http.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            UserAgent = Truncate(req.Headers.UserAgent.ToString(), 512)
        };
    }

    private void SafeEnqueue(DataAppLogEntry entry)
    {
        try { _log.Enqueue(entry); } catch { /* logging must never break the request */ }
    }

    /// <summary>Serializes action arguments to JSON, skipping uploaded files/streams and capping size.</summary>
    private static string SerializeArguments(IDictionary<string, object?> args)
    {
        try
        {
            var clean = new Dictionary<string, object?>();
            foreach (var (key, value) in args)
            {
                switch (value)
                {
                    case null:
                        clean[key] = null;
                        break;
                    case IFormFile f:
                        clean[key] = $"[file:{f.FileName} ({f.Length} bytes)]";
                        break;
                    case IFormFileCollection fc:
                        clean[key] = $"[files:{fc.Count}]";
                        break;
                    case Stream:
                    case byte[]:
                        clean[key] = "[stream]";
                        break;
                    case CancellationToken:
                        break;
                    default:
                        clean[key] = value;
                        break;
                }
            }
            var json = JsonSerializer.Serialize(clean);
            return Truncate(json, 16000);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static object? Arg(IDictionary<string, object?> args, string key)
        => args.TryGetValue(key, out var v) ? v : null;

    /// <summary>Pulls the SQL/query text off a request-body argument (e.g. SqlQueryRequest.Sql).</summary>
    private static string? ExtractSqlFromBody(IDictionary<string, object?> args)
    {
        foreach (var value in args.Values)
        {
            if (value is null || value is string || value.GetType().IsPrimitive) continue;
            foreach (var prop in SqlProps)
            {
                var p = value.GetType().GetProperty(prop);
                if (p?.PropertyType == typeof(string) && p.GetValue(value) is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        return null;
    }

    private static string? Str(object? value) => value?.ToString();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    private static string ToSnake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

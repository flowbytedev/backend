using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Application.Authorization;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Services;
using Application.Shared.Services.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers;

/// <summary>
/// External, API-key authenticated data access. Authentication comes solely from the API key
/// (no cookie/OIDC, no company header) — the key carries its own company, and every action is
/// gated by the key's per-dataset/table read or import grant.
/// </summary>
/// <remarks>
/// <para><b>Which layer a read hits.</b> Every read endpoint takes an optional <c>?source=</c>:
/// <c>snapshot</c> (the default, and the only option for a Local dataset) reads the dataset's own DuckDB
/// file, while <c>live</c> reads the external source database an External dataset is backed by. The two are
/// genuinely different data — an External dataset's DuckDB file holds only the snapshots someone saved into
/// it — so the option exists rather than a guess: omitting it keeps the long-standing behaviour of these
/// endpoints unchanged, and asking for a layer a dataset does not have is an error, never a silent fallback
/// to the other one.</para>
/// <para><b>Acting user.</b> This surface authenticates the <i>integration</i>: the key's
/// <c>ApiKeyScope</c> rows are what bound which tables are readable. A dataset-wide scope row (no
/// <c>TableName</c>) covers the live layer as it does the snapshot; a <i>table-restricted</i> scope must
/// name source tables as <c>{schema}.{name}</c> — the form <c>?source=live</c> returns — or it will match
/// nothing there. An <c>X-User-Id</c> header is optional and can only narrow that — a named user's table grants are applied
/// on the live path, and a named user who carries column grants or RLS filters is refused outright, because
/// neither can be enforced against a live source. Sending no user means no user-level grant is applied, as
/// on every existing endpoint here. A caller that needs column masking and RLS actually enforced must use
/// <c>POST api/dataset/{id}/query/run</c> — the only row-returning path that does (see
/// <c>docs/PUBLIC-SQL-QUERY-API.md</c>).</para>
/// </remarks>
[Route("api/external")]
[ApiController]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationDefaults.Scheme)]
public class ExternalDataController : ControllerBase
{
    private readonly IApiKeyService _apiKeyService;
    private readonly IDuckdbService _duckdbService;
    private readonly IDatabaseTableService _databaseTableService;
    private readonly IDatasetLiveSourceService _liveSource;

    // Same names, same order, as PublicApiControllerBase.UserHeaderNames — one contract for the acting user
    // across both API-key surfaces. Optional here (see the class remarks), so there is no 400 when absent.
    private static readonly string[] UserHeaderNames = { "X-User-Id", "Userid" };

    // Machine-readable refusal code for the live path. The two codes shared with the SQL query API are taken
    // from PublicSqlErrorCodes rather than re-spelled, so a caller handling one surface handles both alike.
    private const string NotAnExternalSourceCode = "not_an_external_source";

    public ExternalDataController(
        IApiKeyService apiKeyService,
        IDuckdbService duckdbService,
        IDatabaseTableService databaseTableService,
        IDatasetLiveSourceService liveSource)
    {
        _apiKeyService = apiKeyService;
        _duckdbService = duckdbService;
        _databaseTableService = databaseTableService;
        _liveSource = liveSource;
    }

    // GET: api/external/datasets/{datasetId}/tables[?source=snapshot|live]
    // Lists only the tables the key is allowed to read.
    [HttpGet("datasets/{datasetId}/tables")]
    public async Task<ActionResult<IEnumerable<string>>> GetTables(
        string datasetId, [FromQuery] string? source, CancellationToken ct = default)
    {
        var key = CurrentKey;
        if (key == null) return Unauthorized();

        // Caller must have at least one read grant somewhere in this dataset.
        if (!_apiKeyService.IsInScope(key, datasetId, null, ApiKeyOperation.Read))
            return Forbid();

        if (!TryReadSource(source, out var live, out var sourceError)) return BadRequest(sourceError);

        if (live)
        {
            var (read, denied) = await ResolveLiveAsync(datasetId, ct);
            if (denied != null) return denied;

            var discovery = await _databaseTableService.DiscoverTablesAsync(read!.SourceEntityId!, key.CompanyId, ct);
            // Surface a listing failure rather than an empty list — "no tables" and "could not reach the
            // source" are very different answers.
            if (!string.IsNullOrEmpty(discovery.Error)) return BadRequest(discovery.Error);

            return Ok(discovery.Tables
                .Select(t => t.FullName)
                .Where(t => _apiKeyService.IsInScope(key, datasetId, t, ApiKeyOperation.Read))
                .Where(read.MayReadTable)
                .ToList());
        }

        var tables = (await _duckdbService.GetTablesAsync(datasetId) ?? Enumerable.Empty<string>())
            .Where(t => _apiKeyService.IsInScope(key, datasetId, t, ApiKeyOperation.Read))
            .ToList();

        return Ok(tables);
    }

    // GET: api/external/datasets/{datasetId}/tables/{tableName}/columns[?source=snapshot|live]
    [HttpGet("datasets/{datasetId}/tables/{tableName}/columns")]
    public async Task<ActionResult<IEnumerable<Column>>> GetColumns(
        string datasetId, string tableName, [FromQuery] string? source, CancellationToken ct = default)
    {
        var denied = CheckScope(datasetId, tableName, ApiKeyOperation.Read);
        if (denied != null) return denied;

        if (!TryReadSource(source, out var live, out var sourceError)) return BadRequest(sourceError);

        try
        {
            if (live)
            {
                var (read, liveDenied) = await ResolveLiveAsync(datasetId, ct);
                if (liveDenied != null) return liveDenied;
                if (!read!.MayReadTable(tableName)) return TableNotPermitted(tableName);

                // Schema-only read: the column shape without executing the query or transferring rows.
                // Nullability and primary keys do not come back from it — the same gap the data catalog
                // documents for live sources (docs/PUBLIC-API-MIGRATION-HANDOFF.md section 5b).
                var schema = await _databaseTableService.GetTableSchemaAsync(
                    read.SourceEntityId!, CurrentKey!.CompanyId, tableName, ct);
                if (!string.IsNullOrEmpty(schema.Error)) return BadRequest(schema.Error);

                return Ok(schema.Columns);
            }

            return await _duckdbService.GetTableColumnsAsync(datasetId, tableName);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error retrieving columns: {ex.Message}");
        }
    }

    // POST: api/external/datasets/{datasetId}/tables/{tableName}/data[?source=snapshot|live][&includeTotal=]
    // Paged + filtered row query (same engine the UI uses).
    [HttpPost("datasets/{datasetId}/tables/{tableName}/data")]
    public async Task<ActionResult<TableDataResult>> GetData(
        string datasetId, string tableName, [FromBody] TableDataQuery? query,
        [FromQuery] string? source, [FromQuery] bool includeTotal = true, CancellationToken ct = default)
    {
        var denied = CheckScope(datasetId, tableName, ApiKeyOperation.Read);
        if (denied != null) return denied;

        if (!TryReadSource(source, out var live, out var sourceError)) return BadRequest(sourceError);

        try
        {
            query ??= new TableDataQuery();
            query.DatasetId = datasetId;
            query.TableName = tableName;
            if (query.Page <= 0) query.Page = 1;
            if (query.PageSize <= 0) query.PageSize = 100;

            if (live)
            {
                var (read, liveDenied) = await ResolveLiveAsync(datasetId, ct);
                if (liveDenied != null) return liveDenied;
                if (!read!.MayReadTable(tableName)) return TableNotPermitted(tableName);

                var external = await _databaseTableService.QueryTableDataAsync(
                    read.SourceEntityId!, CurrentKey!.CompanyId, query, includeTotal, ct);
                if (!string.IsNullOrEmpty(external.Error)) return BadRequest(external.Error);

                // totalRows is 0 (not a count) when the caller opted out of the COUNT round trip; say so in
                // a header rather than letting a 0 read as "no rows".
                if (!includeTotal) Response.Headers["X-Total-Rows-Omitted"] = "true";
                if (external.Truncated) Response.Headers["X-Rows-Truncated"] = "true";

                return Ok(external.Data);
            }

            var result = await _duckdbService.QueryTableDataAsync(query);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error querying table data: {ex.Message}");
        }
    }

    // GET: api/external/datasets/{datasetId}/tables/{tableName}/download[?source=snapshot|live]
    [HttpGet("datasets/{datasetId}/tables/{tableName}/download")]
    public async Task<ActionResult> Download(
        string datasetId, string tableName, [FromQuery] string? source, CancellationToken ct = default)
    {
        var denied = CheckScope(datasetId, tableName, ApiKeyOperation.Read);
        if (denied != null) return denied;

        if (!TryReadSource(source, out var live, out var sourceError)) return BadRequest(sourceError);

        try
        {
            var query = new TableDataQuery
            {
                DatasetId = datasetId,
                TableName = tableName,
                Page = 1,
                PageSize = int.MaxValue, // full table for download
            };

            TableDataResult? data;

            if (live)
            {
                var (read, liveDenied) = await ResolveLiveAsync(datasetId, ct);
                if (liveDenied != null) return liveDenied;
                if (!read!.MayReadTable(tableName)) return TableNotPermitted(tableName);

                // A live download is NOT necessarily the whole table: the page size is clamped to the
                // external row ceiling, so a big source table comes back cut off. The header is the only
                // honest way to say so without corrupting the CSV. The total count is skipped — it would
                // cost a full COUNT(*) on the source for a number that never reaches the caller.
                var external = await _databaseTableService.QueryTableDataAsync(
                    read.SourceEntityId!, CurrentKey!.CompanyId, query, includeTotalRows: false, ct);
                if (!string.IsNullOrEmpty(external.Error)) return BadRequest(external.Error);

                data = external.Data;
                if (external.Truncated) Response.Headers["X-Rows-Truncated"] = "true";
            }
            else
            {
                data = await _duckdbService.QueryTableDataAsync(query);
            }

            if (data?.Data == null || !data.Data.Any())
                return NotFound($"No data found for table '{tableName}'.");

            var bytes = Encoding.UTF8.GetBytes(BuildCsv(data));
            return File(bytes, "text/csv", $"{tableName}.csv");
        }
        catch (Exception ex)
        {
            return BadRequest($"Error downloading table data: {ex.Message}");
        }
    }

    // POST: api/external/datasets/{datasetId}/tables/{tableName}/import
    // Appends CSV rows into an existing table. Requires an import grant for the table.
    // Snapshot layer only, and there is no ?source= here: an external database source is opened read-only
    // by every path in this app, so there is nothing to write into.
    [HttpPost("datasets/{datasetId}/tables/{tableName}/import")]
    [RequestSizeLimit(300_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300_000_000)]
    public async Task<ActionResult> Import(string datasetId, string tableName)
    {
        var denied = CheckScope(datasetId, tableName, ApiKeyOperation.Import);
        if (denied != null) return denied;

        try
        {
            if (!Request.HasFormContentType)
                return BadRequest("Request must be multipart/form-data with a CSV file field named 'file'.");

            var form = await Request.ReadFormAsync();
            if (form.Files.Count == 0)
                return BadRequest("CSV file is required");

            var csvFile = form.Files[0];
            if (csvFile.Length == 0)
                return BadRequest("CSV file cannot be empty");

            if (!csvFile.ContentType.StartsWith("text/csv") &&
                !Path.GetExtension(csvFile.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                return BadRequest("File must be a CSV file");

            using var stream = csvFile.OpenReadStream();
            var ok = await _duckdbService.ImportCsvDataAsync(datasetId, tableName, stream);
            if (!ok) return StatusCode(500, "Failed to import CSV data");

            return Ok(new { message = "CSV data imported successfully", datasetId, tableName });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error importing CSV data: {ex.Message}");
        }
    }

    // ---- layer selection ----------------------------------------------------------------------

    /// <summary>
    /// Reads the <c>?source=</c> option. Absent or blank means the local snapshot, so an existing caller's
    /// behaviour is unchanged. An unrecognised value is an error rather than a default, because silently
    /// reading the snapshot when someone typed <c>?source=liv</c> is the failure mode the option exists to
    /// prevent.
    /// </summary>
    private static bool TryReadSource(string? source, out bool live, out string? error)
    {
        live = false;
        error = null;

        if (string.IsNullOrWhiteSpace(source)) return true;

        switch (source.Trim().ToLowerInvariant())
        {
            case "snapshot":
            case "local":
            case "duckdb":
                return true;
            case "live":
            case "external":
            case "source":
                live = true;
                return true;
            default:
                error = $"Unknown source '{source}'. Use 'snapshot' (the default, reads the dataset's local " +
                        "tables) or 'live' (reads the external database an External dataset is backed by).";
                return false;
        }
    }

    /// <summary>
    /// Resolves the dataset's live source for this request, or the <see cref="ActionResult"/> to return
    /// instead. Exactly one of the two is non-null.
    /// </summary>
    private async Task<(LiveSourceRead? Read, ActionResult? Denied)> ResolveLiveAsync(
        string datasetId, CancellationToken ct)
    {
        var read = await _liveSource.ResolveReadAsync(CurrentKey!.CompanyId, datasetId, ActingUserId, ct);
        if (read.Allowed) return (read, null);

        ActionResult denied = read.Outcome switch
        {
            LiveSourceReadOutcome.DatasetNotFound => NotFound(read.Error),
            LiveSourceReadOutcome.NotExternalSource =>
                BadRequest(new { errorCode = NotAnExternalSourceCode, message = read.Error }),
            // 403 with a code in the body: a fail-closed security refusal and a missing key scope are both
            // "you may not have this", but they are different people's problems — the code is what lets a
            // caller tell "retry against the snapshot" from "an operator must widen the key".
            LiveSourceReadOutcome.SecurityNotEnforceable => StatusCode(StatusCodes.Status403Forbidden,
                new { errorCode = PublicSqlErrorCodes.SecurityNotEnforceable, message = read.Error }),
            LiveSourceReadOutcome.UserNotPermitted => StatusCode(StatusCodes.Status403Forbidden,
                new { errorCode = PublicSqlErrorCodes.TableNotPermitted, message = read.Error }),
            _ => StatusCode(StatusCodes.Status403Forbidden, new { message = read.Error })
        };

        return (null, denied);
    }

    /// <summary>The acting end user, when the caller named one. Null when it did not — see the class remarks.</summary>
    private string? ActingUserId
    {
        get
        {
            foreach (var name in UserHeaderNames)
            {
                var candidate = Request.Headers[name].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
            }
            return null;
        }
    }

    private ActionResult TableNotPermitted(string tableName) =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            errorCode = PublicSqlErrorCodes.TableNotPermitted,
            message = $"Table '{tableName}' is outside the acting user's grants for this dataset."
        });

    /// <summary>
    /// Renders a result grid as CSV. Dates are written as fixed ISO 8601 here, NOT the company's display
    /// format: this is a machine-facing API-key export, so its shape must not shift when someone changes the
    /// export format in the settings UI. (A bare ToString() also made it server-culture dependent.)
    /// </summary>
    private static string BuildCsv(TableDataResult data)
    {
        var headers = data.Columns?.Any() == true
            ? data.Columns.Select(c => c.Name).ToList()
            : data.Data.First().Keys.ToList();

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

        foreach (var row in data.Data)
        {
            var values = headers.Select(h =>
            {
                var v = row.TryGetValue(h, out var cell) ? cell : null;
                return CsvExportFormatter.Field(v, CsvExportFormatter.IsoDateFormat);
            });
            csv.AppendLine(string.Join(",", values));
        }

        return csv.ToString();
    }

    // ---- scope plumbing ------------------------------------------------------------------------

    private ApiKey? CurrentKey =>
        HttpContext.Items.TryGetValue(ApiKeyAuthenticationDefaults.ApiKeyItem, out var v) ? v as ApiKey : null;

    /// <summary>Returns a 401/403 result when the current key may not perform the operation; otherwise null.</summary>
    private ActionResult? CheckScope(string datasetId, string? tableName, ApiKeyOperation operation)
    {
        var key = CurrentKey;
        if (key == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(datasetId)) return BadRequest("Dataset ID is required");
        if (!_apiKeyService.IsInScope(key, datasetId, tableName, operation)) return Forbid();
        return null;
    }
}

using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Turns a relation into a file on disk — the half of <c>destination.email</c> that has nothing to do with
/// email. Split out so the two concerns can fail separately and be tested separately: "the workbook is
/// wrong" and "the mail did not go" are different bugs with different fixes.
/// <para>
/// Two formats take two entirely different routes, and the asymmetry is forced rather than chosen:
/// </para>
/// <list type="bullet">
/// <item><b>CSV and JSON</b> go through DuckDB's own <c>COPY … TO</c>. It streams, so row count is bounded
/// by disk rather than by memory, and its quoting is the same code that reads these files back.</item>
/// <item><b>XLSX</b> is built in-process with ClosedXML, because DuckDB's xlsx writer needs the
/// <c>excel</c> extension and that extension cannot be installed on a machine that has not already cached
/// it — see <see cref="PipelineExportFormats.Xlsx"/>. It holds the sheet in memory, which is acceptable
/// only because an emailed workbook is size-capped anyway.</item>
/// </list>
/// </summary>
public interface IPipelineExportWriter
{
    Task<ExportFileResult> WriteAsync(ExportFileRequest request, CancellationToken ct = default);
}

public sealed class ExportFileRequest
{
    /// <summary>The scratch dataset and relation holding the rows to export.</summary>
    public required string SourceDatasetId { get; init; }
    public required string SourceRelation { get; init; }

    /// <summary>Directory the file is written into. The caller owns cleaning it up.</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>File name without an extension. The format decides the extension.</summary>
    public required string BaseName { get; init; }

    /// <summary>One of <see cref="PipelineExportFormats"/>.</summary>
    public string Format { get; init; } = PipelineExportFormats.Csv;

    /// <summary>CSV only. A single character; a tab is written as <c>\t</c> in config.</summary>
    public string Delimiter { get; init; } = ",";

    /// <summary>CSV only.</summary>
    public bool IncludeHeader { get; init; } = true;

    /// <summary>XLSX only. Excel's own 31-character limit is enforced here, not left to ClosedXML.</summary>
    public string SheetName { get; init; } = "Data";

    /// <summary>Wrap the result in a zip. A delimited export typically shrinks by 5-10x.</summary>
    public bool Compress { get; init; }

    /// <summary>
    /// Refuse to write more than this many rows. A guard, not a page size: the file is going into an email,
    /// and a workbook is assembled in memory before it is saved.
    /// </summary>
    public long MaxRows { get; init; } = 1_000_000;

    public IJobProgress? Progress { get; init; }
}

public sealed record ExportFileResult(
    bool Success,
    string? Path,
    long Bytes,
    long Rows,
    string? Error,
    string? ErrorType)
{
    public static ExportFileResult Ok(string path, long bytes, long rows) =>
        new(true, path, bytes, rows, null, null);

    public static ExportFileResult Fail(string error, string errorType) =>
        new(false, null, 0, 0, error, errorType);
}

public class PipelineExportWriter(IPipelineStore store) : IPipelineExportWriter
{
    /// <summary>
    /// Beyond this magnitude a whole number stops being exactly representable as an IEEE double, which is
    /// the only numeric type a spreadsheet cell has. Measured, not assumed: <c>Int64.MaxValue</c> written as
    /// a number reads back as 9.22337203685478E+18. Anything above this goes into the cell as text, because
    /// a silently rounded order number is worse than one that cannot be summed.
    /// </summary>
    private const long MaxExactInteger = 9_007_199_254_740_992; // 2^53

    public async Task<ExportFileResult> WriteAsync(ExportFileRequest request, CancellationToken ct = default)
    {
        Directory.CreateDirectory(request.TargetDirectory);

        var extension = PipelineExportFormats.Extension(request.Format);
        var path = Path.Combine(request.TargetDirectory, $"{Sanitize(request.BaseName)}.{extension}");

        try
        {
            var rows = request.Format == PipelineExportFormats.Xlsx
                ? await WriteWorkbookAsync(request, path, ct)
                : await CopyOutAsync(request, path, ct);

            if (rows < 0)
            {
                return ExportFileResult.Fail(
                    $"This export would be more than {request.MaxRows:N0} rows, which is more than can be "
                    + "sent as an attachment. Filter the rows down, or write to a dataset instead.",
                    PipelineErrorType.Invalid);
            }

            if (!File.Exists(path))
            {
                return ExportFileResult.Fail(
                    "The export produced no file.", PipelineErrorType.SqlError);
            }

            if (request.Compress) path = Zip(path);

            return ExportFileResult.Ok(path, new FileInfo(path).Length, rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            return ExportFileResult.Fail($"The export failed: {ex.Message}", PipelineErrorType.SqlError);
        }
    }

    /// <summary>
    /// CSV / JSON via DuckDB. Returns the rows written, or -1 when the relation exceeds
    /// <see cref="ExportFileRequest.MaxRows"/>.
    /// </summary>
    private async Task<long> CopyOutAsync(ExportFileRequest request, string path, CancellationToken ct)
    {
        // Counted before the COPY rather than after, so an oversized relation is refused without first
        // writing a multi-gigabyte file to disk and then deciding not to send it.
        var count = await store.ReadScalarAsync(
            request.SourceDatasetId,
            $"SELECT count(*) FROM {Quote(request.SourceRelation)}",
            ct: ct);

        if (!count.Success)
            throw new InvalidOperationException(count.Error ?? "The row count could not be read.");

        var rows = Convert.ToInt64(count.Value ?? 0L, CultureInfo.InvariantCulture);
        if (rows > request.MaxRows) return -1;

        var copied = await store.ExportRelationToFileAsync(
            request.SourceDatasetId, request.SourceRelation, path, request.Format,
            request.IncludeHeader, ResolveDelimiter(request.Delimiter), ct: ct);

        if (!copied.Success)
            throw new InvalidOperationException(copied.Error ?? "The COPY failed.");

        request.Progress?.WriteLine($"      wrote {rows:N0} row(s) to {Path.GetFileName(path)}");
        return rows;
    }

    /// <summary>
    /// XLSX via ClosedXML, streamed off a live DuckDB reader into the sheet. Returns the data rows written,
    /// or -1 if the relation is longer than the cap — checked while reading, so an oversized relation costs
    /// one pass and not a whole workbook.
    /// </summary>
    private async Task<long> WriteWorkbookAsync(ExportFileRequest request, string path, CancellationToken ct)
    {
        return await store.ReadRelationAsync(
            request.SourceDatasetId, request.SourceRelation,
            (reader, columns, token) =>
            {
                using var workbook = new XLWorkbook();
                var sheet = workbook.AddWorksheet(SheetName(request.SheetName));

                for (var i = 0; i < columns.Count; i++)
                {
                    sheet.Cell(1, i + 1).Value = columns[i].Name;
                    sheet.Cell(1, i + 1).Style.Font.Bold = true;
                }

                long rows = 0;
                var rowIndex = 2;

                while (reader.Read())
                {
                    if (++rows > request.MaxRows) return Task.FromResult(-1L);

                    for (var i = 0; i < columns.Count; i++)
                    {
                        if (reader.IsDBNull(i)) continue;
                        Set(sheet.Cell(rowIndex, i + 1), reader.GetValue(i));
                    }

                    rowIndex++;
                    token.ThrowIfCancellationRequested();
                }

                // Freeze the header and size the columns only once the sheet is populated — AdjustToContents
                // measures what is there.
                sheet.SheetView.FreezeRows(1);
                sheet.Columns().AdjustToContents();
                workbook.SaveAs(path);

                request.Progress?.WriteLine(
                    $"      wrote {rows:N0} row(s) to {Path.GetFileName(path)}");

                return Task.FromResult(rows);
            },
            ct);
    }

    /// <summary>
    /// One DuckDB value into one cell, typed.
    /// <para>
    /// Every string goes in through <c>SetValue</c> rather than <c>Value</c>, and that is the important line
    /// in this file. <c>Value</c> parses what it is given, so a cell holding <c>=1+1</c> or
    /// <c>=HYPERLINK(...)</c> would become a live formula in the recipient's Excel, and a code like
    /// <c>0041</c> would lose its leading zeros. <c>SetValue</c> stores text as text.
    /// </para>
    /// </summary>
    private static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return;
            case bool b:
                cell.Value = b;
                return;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = dt.TimeOfDay == TimeSpan.Zero
                    ? "yyyy-mm-dd"
                    : "yyyy-mm-dd hh:mm:ss";
                return;
            case DateTimeOffset dto:
                cell.Value = dto.UtcDateTime;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                return;
            case DateOnly date:
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                return;
            case TimeOnly time:
                cell.Value = time.ToTimeSpan();
                cell.Style.DateFormat.Format = "hh:mm:ss";
                return;
            case TimeSpan span:
                cell.Value = span;
                cell.Style.DateFormat.Format = "hh:mm:ss";
                return;
            case decimal m:
                cell.Value = m;
                return;
            case double d:
                cell.Value = d;
                return;
            case float f:
                cell.Value = f;
                return;
            case byte or sbyte or short or ushort or int or uint:
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return;
            case long or ulong:
            {
                // Exact past 2^53, text beyond it. See MaxExactInteger.
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                var magnitude = value is ulong u
                    ? (u > MaxExactInteger ? MaxExactInteger + 1 : (long)u)
                    : Math.Abs((long)value);

                if (magnitude > MaxExactInteger) cell.SetValue(text);
                else cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return;
            }
            case Guid g:
                cell.SetValue(g.ToString());
                return;
            case byte[] bytes:
                cell.SetValue(Convert.ToBase64String(bytes));
                return;
            case string s:
                cell.SetValue(s);
                return;
            default:
                cell.SetValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                return;
        }
    }

    /// <summary>
    /// Zips the file in place and removes the original. The entry keeps the uncompressed name, so what the
    /// recipient extracts is exactly what would have been attached.
    /// </summary>
    private static string Zip(string path)
    {
        var zipPath = path + ".zip";
        TryDelete(zipPath);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);

        TryDelete(path);
        return zipPath;
    }

    /// <summary>
    /// <c>\t</c> in config means a tab — the field is a text box, so the character cannot be typed into it.
    /// DuckDB's CSV writer takes exactly one character.
    /// </summary>
    internal static string ResolveDelimiter(string? configured)
    {
        if (string.IsNullOrEmpty(configured)) return ",";

        var resolved = configured switch
        {
            "\\t" or "tab" or "\t" => "\t",
            "\\0" => ",",
            _ => configured
        };

        return resolved.Length == 1 ? resolved : resolved[..1];
    }

    /// <summary>
    /// Excel refuses a sheet name over 31 characters or containing <c>: \ / ? * [ ]</c>, and refuses to open
    /// the whole workbook rather than ignoring the name — so this is a correctness step, not tidying.
    /// </summary>
    internal static string SheetName(string? requested)
    {
        var name = string.IsNullOrWhiteSpace(requested) ? "Data" : requested!.Trim();

        foreach (var bad in new[] { ':', '\\', '/', '?', '*', '[', ']' })
            name = name.Replace(bad, '_');

        // A leading or trailing apostrophe is also rejected.
        name = name.Trim('\'');

        if (name.Length > 31) name = name[..31];
        return name.Length == 0 ? "Data" : name;
    }

    /// <summary>
    /// Strips what a file name cannot carry. Also strips directory separators and leading dots, so a
    /// token-built name cannot walk out of the export directory.
    /// </summary>
    internal static string Sanitize(string? baseName)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "export" : baseName!.Trim();

        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        name = name.Replace('/', '_').Replace('\\', '_').TrimStart('.', ' ').TrimEnd(' ', '.');

        if (name.Length > 120) name = name[..120];
        return name.Length == 0 ? "export" : name;
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

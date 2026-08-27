using Azure.Storage.Blobs;
using Application.Shared.Models.Data;
using Application.Shared.Models.Data.Pipelines;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Turns a <c>source.file</c> step's settings into a real file on local disk that DuckDB can read.
/// <para>
/// Three locations, and the difference between them is entirely about <em>when the file exists</em>: a
/// folder or a blob is there whenever the schedule fires, an upload only exists for the run it was attached
/// to. That is why the compiler refuses to schedule a pipeline containing an upload source, and why this
/// service treats a missing upload as a configuration error rather than a missing file.
/// </para>
/// </summary>
public interface IPipelineFileResolver
{
    Task<ResolvedFile> ResolveAsync(
        PipelineFileRequest request, CancellationToken ct = default);

    /// <summary>
    /// Runs after a successful run, moving a folder source's file aside so tomorrow's run does not read it
    /// again. Best-effort by design: the data is already loaded, so a failure to tidy up must not fail the
    /// run.
    /// </summary>
    Task ArchiveAsync(ResolvedFile file, string? archiveTo, CancellationToken ct = default);
}

/// <summary>What a file source is asking for, with tokens already substituted.</summary>
public sealed class PipelineFileRequest
{
    public required string Location { get; init; }
    public string? Path { get; init; }
    public string? Pick { get; init; }
    public string? Container { get; init; }
    public string? BlobPath { get; init; }

    /// <summary>The file a manual run attached, already written to disk by the controller.</summary>
    public string? UploadedPath { get; init; }
}

/// <summary>
/// A local path DuckDB can read, plus what may need cleaning up afterwards.
/// </summary>
public sealed record ResolvedFile(
    bool Success,
    string? LocalPath,
    string? Error,
    string? ErrorType,
    /// <summary>Where the file came from, for the archive step and for the run log.</summary>
    string? OriginalPath = null,
    /// <summary>True when LocalPath is a temporary copy this run owns and must delete.</summary>
    bool IsTemporary = false)
{
    public static ResolvedFile Ok(string localPath, string? originalPath = null, bool isTemporary = false) =>
        new(true, localPath, null, null, originalPath ?? localPath, isTemporary);

    public static ResolvedFile Fail(string error, string errorType) =>
        new(false, null, error, errorType);
}

public class PipelineFileResolver(PipelineOptions options, AzureBlobOption blobOption) : IPipelineFileResolver
{
    public async Task<ResolvedFile> ResolveAsync(PipelineFileRequest request, CancellationToken ct = default)
    {
        return request.Location switch
        {
            PipelineFileLocations.Folder => ResolveFolder(request),
            PipelineFileLocations.Blob => await ResolveBlobAsync(request, ct),
            PipelineFileLocations.Upload => ResolveUpload(request),
            _ => ResolvedFile.Fail(
                $"'{request.Location}' is not a file location this build understands.",
                PipelineErrorType.Invalid)
        };
    }

    // ---------------------------------------------------------------- folder

    private ResolvedFile ResolveFolder(PipelineFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return ResolvedFile.Fail("This step has no path.", PipelineErrorType.Invalid);

        var raw = request.Path!.Trim();

        string directory;
        string pattern;
        try
        {
            directory = Path.GetDirectoryName(raw) ?? string.Empty;
            pattern = Path.GetFileName(raw);
        }
        catch (ArgumentException ex)
        {
            return ResolvedFile.Fail($"'{raw}' is not a usable path: {ex.Message}", PipelineErrorType.Invalid);
        }

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(pattern))
            return ResolvedFile.Fail(
                $"'{raw}' needs to be a full path to a file, or a folder plus a pattern like *.xlsx.",
                PipelineErrorType.Invalid);

        if (!IsAllowed(directory))
            return ResolvedFile.Fail(
                $"Reading from '{directory}' is not permitted. Allowed folders: " +
                string.Join(", ", options.AllowedSourceDirectories) + ".",
                PipelineErrorType.Invalid);

        if (!Directory.Exists(directory))
            return ResolvedFile.Fail($"The folder '{directory}' does not exist or is not reachable.",
                PipelineErrorType.SourceUnavailable);

        // A path with no wildcard is just a file; skip the enumeration so a missing file reports as itself.
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return File.Exists(raw)
                ? ResolvedFile.Ok(raw)
                : ResolvedFile.Fail($"The file '{raw}' was not found.", PipelineErrorType.SourceUnavailable);
        }

        List<FileInfo> matches;
        try
        {
            matches = new DirectoryInfo(directory)
                .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (Exception ex)
        {
            return ResolvedFile.Fail($"Could not read '{directory}': {ex.Message}",
                PipelineErrorType.SourceUnavailable);
        }

        if (matches.Count == 0)
            return ResolvedFile.Fail($"No file in '{directory}' matches '{pattern}'.",
                PipelineErrorType.SourceUnavailable);

        // "all" would mean combining several files into one relation. DuckDB can do that natively by
        // handing the glob straight to the reader, which is both faster and simpler than concatenating.
        if (string.Equals(request.Pick, "all", StringComparison.OrdinalIgnoreCase))
            return ResolvedFile.Ok(raw);

        var newest = matches.OrderByDescending(f => f.LastWriteTimeUtc).First();
        return ResolvedFile.Ok(newest.FullName);
    }

    /// <summary>
    /// An empty allow-list means "anywhere the service account can reach". That is the pragmatic default for
    /// an on-premise install, where the drop folders are on the same file server and enumerating them all in
    /// config would be a maintenance burden nobody keeps up with.
    /// </summary>
    private bool IsAllowed(string directory)
    {
        if (options.AllowedSourceDirectories.Count == 0) return true;

        var candidate = Normalize(directory);
        return options.AllowedSourceDirectories
            .Select(Normalize)
            .Any(allowed => candidate.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path)
    {
        var trimmed = (path ?? string.Empty).Trim().TrimEnd('/', '\\');
        try { return Path.GetFullPath(trimmed).TrimEnd('/', '\\'); }
        catch { return trimmed; }
    }

    // ------------------------------------------------------------------ blob

    private async Task<ResolvedFile> ResolveBlobAsync(PipelineFileRequest request, CancellationToken ct)
    {
        if (!blobOption.IsConfigured)
            return ResolvedFile.Fail(
                "Azure Blob storage is not configured on this server (AzureBlob:ConnectionString).",
                PipelineErrorType.Invalid);

        var container = string.IsNullOrWhiteSpace(request.Container)
            ? blobOption.ContainerName
            : request.Container;

        if (string.IsNullOrWhiteSpace(container))
            return ResolvedFile.Fail("This step has no container.", PipelineErrorType.Invalid);

        if (string.IsNullOrWhiteSpace(request.BlobPath))
            return ResolvedFile.Fail("This step has no blob path.", PipelineErrorType.Invalid);

        // Downloaded to local disk rather than read in place: DuckDB's azure extension is not installed
        // here, and a whole-file download is honest about what is happening anyway.
        var temp = Path.Combine(Path.GetTempPath(),
            $"pipe_blob_{Guid.NewGuid():N}{Path.GetExtension(request.BlobPath) ?? string.Empty}");

        try
        {
            var client = new BlobContainerClient(blobOption.ConnectionString, container);
            var blob = client.GetBlobClient(request.BlobPath);

            if (!await blob.ExistsAsync(ct))
                return ResolvedFile.Fail(
                    $"'{request.BlobPath}' was not found in container '{container}'.",
                    PipelineErrorType.SourceUnavailable);

            await using (var target = File.Create(temp))
                await blob.DownloadToAsync(target, ct);

            return ResolvedFile.Ok(temp, $"{container}/{request.BlobPath}", isTemporary: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return ResolvedFile.Fail($"Could not download '{request.BlobPath}': {ex.Message}",
                PipelineErrorType.SourceUnavailable);
        }
    }

    // ---------------------------------------------------------------- upload

    private static ResolvedFile ResolveUpload(PipelineFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UploadedPath))
            return ResolvedFile.Fail(
                "This step reads a file uploaded at run time, but no file was attached to this run. " +
                "Start it from the editor and choose a file.",
                PipelineErrorType.Invalid);

        return File.Exists(request.UploadedPath)
            ? ResolvedFile.Ok(request.UploadedPath!, isTemporary: true)
            : ResolvedFile.Fail("The uploaded file is no longer available.",
                PipelineErrorType.SourceUnavailable);
    }

    // --------------------------------------------------------------- archive

    public Task ArchiveAsync(ResolvedFile file, string? archiveTo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(archiveTo) || file.OriginalPath is null || file.IsTemporary)
            return Task.CompletedTask;

        try
        {
            // A glob was handed straight to DuckDB, so there is no single file to move.
            if (file.OriginalPath.Contains('*') || file.OriginalPath.Contains('?')) return Task.CompletedTask;
            if (!File.Exists(file.OriginalPath)) return Task.CompletedTask;

            Directory.CreateDirectory(archiveTo!);

            var name = Path.GetFileName(file.OriginalPath);
            var destination = Path.Combine(archiveTo!, name);

            // Never overwrite an archived file — it is the only remaining copy of what was loaded.
            if (File.Exists(destination))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var extension = Path.GetExtension(name);
                destination = Path.Combine(archiveTo!,
                    $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{extension}");
            }

            File.Move(file.OriginalPath, destination);
        }
        catch
        {
            // Deliberately swallowed. The rows are already committed; failing the run because a file could
            // not be moved would report a successful load as a failure.
        }

        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

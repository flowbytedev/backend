using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Application.Services.Data;

/// <summary>One prepared CSV export sitting on disk, waiting for the browser to collect it.</summary>
/// <param name="Rows">Data rows written.</param>
public sealed record TableExportTicket(
    string Token,
    string FilePath,
    string FileName,
    string UserId,
    string CompanyId,
    long Rows,
    long Bytes);

/// <summary>
/// Hands out short-lived, single-use tokens for prepared table exports.
/// <para>
/// Exists because of a mismatch between how the app authorizes and how browsers download. Every dataset
/// endpoint takes its tenant from the <c>X-Company-ID</c> header and its user from <c>UserId</c>, but a
/// browser navigating to a URL — which is the only way to get a large file downloaded without routing it
/// through the WebAssembly heap — cannot set headers. So authorization happens on a POST that does carry
/// them, and the GET that streams the file redeems a token which already encodes the decided context.
/// </para>
/// <para>
/// The token is not the only check: <see cref="Redeem"/> also requires the collecting user to be the one
/// the ticket was issued to, so a leaked token is useless to another signed-in account.
/// </para>
/// </summary>
public interface ITableExportTicketStore
{
    /// <summary>Registers a written file and returns its ticket.</summary>
    TableExportTicket Issue(string filePath, string fileName, string userId, string companyId, long rows);

    /// <summary>
    /// Consumes a token. Returns null when it is unknown, already used, expired, or was issued to a
    /// different user — all reported to the caller the same way, so a probe learns nothing from the
    /// difference.
    /// </summary>
    TableExportTicket? Redeem(string token, string userId);

    /// <summary>Best-effort delete of one collected file, once the response has finished writing it.</summary>
    void Discard(TableExportTicket ticket);

    /// <summary>
    /// Deletes export files in <paramref name="directory"/> older than the ticket lifetime. Cache eviction
    /// callbacks are lazy and do not run at all if the process restarts, so without this an abandoned
    /// download would leave a multi-gigabyte file on the server indefinitely.
    /// </summary>
    void SweepStale(string directory);
}

public sealed class TableExportTicketStore : ITableExportTicketStore
{
    /// <summary>
    /// Long enough for a big export to be collected over a slow link, short enough that an abandoned one
    /// is not occupying disk for the rest of the day.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Prefix every export file carries, so <see cref="SweepStale"/> can tell this feature's leftovers
    /// from anything else sharing the working folder (pipeline staging files use <c>pl_</c>).
    /// </summary>
    public const string FilePrefix = "export";

    private readonly IMemoryCache _cache;

    public TableExportTicketStore(IMemoryCache cache) => _cache = cache;

    public TableExportTicket Issue(string filePath, string fileName, string userId, string companyId, long rows)
    {
        var bytes = 0L;
        try { bytes = new FileInfo(filePath).Length; } catch { /* reported as unknown, not fatal */ }

        var ticket = new TableExportTicket(
            Token: NewToken(),
            FilePath: filePath,
            FileName: fileName,
            UserId: userId,
            CompanyId: companyId,
            Rows: rows,
            Bytes: bytes);

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime };

        // Fires when the ticket expires or is evicted under pressure — i.e. nobody collected the file.
        // Explicitly NOT on EvictionReason.Removed: that is Redeem taking the ticket out on its way to
        // streaming the file, and deleting it there would race the response.
        options.RegisterPostEvictionCallback((_, value, reason, _) =>
        {
            if (reason != EvictionReason.Removed && value is TableExportTicket abandoned)
                Delete(abandoned.FilePath);
        });

        _cache.Set(CacheKey(ticket.Token), ticket, options);
        return ticket;
    }

    public TableExportTicket? Redeem(string token, string userId)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userId))
            return null;

        if (!_cache.TryGetValue(CacheKey(token), out TableExportTicket? ticket) || ticket is null)
            return null;

        if (!string.Equals(ticket.UserId, userId, StringComparison.Ordinal))
            return null;

        // Single use: remove before streaming, so a token that leaks out of the URL bar cannot be replayed.
        _cache.Remove(CacheKey(token));

        // The cache can outlive the file if the folder was cleared underneath us.
        return File.Exists(ticket.FilePath) ? ticket : null;
    }

    public void Discard(TableExportTicket ticket) => Delete(ticket.FilePath);

    public void SweepStale(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        try
        {
            var cutoff = DateTime.UtcNow - Lifetime;
            foreach (var file in Directory.EnumerateFiles(directory, FilePrefix + "_*.csv"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* in use by an in-flight download, or gone already */ }
            }
        }
        catch { /* the sweep is opportunistic; never fail an export because tidying failed */ }
    }

    private static string CacheKey(string token) => "table-export:" + token;

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* swept later */ }
    }

    /// <summary>256 bits of randomness, base64url so it is safe in a path segment without escaping.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

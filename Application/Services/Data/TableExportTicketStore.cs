using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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
    /// Looks a token up and checks it belongs to this collector. Returns null when it is unknown, expired,
    /// already collected, or was issued to a different user or company — all reported the same way, so a
    /// probe learns nothing from the difference.
    /// <para>
    /// Deliberately does NOT consume the ticket; <see cref="Complete"/> does, once a response has actually
    /// been delivered. Consuming on lookup sounds safer and is in fact brittle: a browser can issue the
    /// same download request more than once (a probe then a fetch, or a client-side navigation that is
    /// abandoned and retried), and the user then sees "already used" for a file they never received. The
    /// window this opens is small and uninteresting — the token is 256-bit, bound to one user and company,
    /// expires, and a replay only re-fetches that same person's own file.
    /// </para>
    /// </summary>
    TableExportTicket? Resolve(string token, string userId, string companyId);

    /// <summary>
    /// Retires a ticket once its response has finished writing: the token stops working and the file is
    /// deleted.
    /// </summary>
    void Complete(TableExportTicket ticket);

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
    private readonly ILogger<TableExportTicketStore> _log;

    public TableExportTicketStore(IMemoryCache cache, ILogger<TableExportTicketStore> log)
    {
        _cache = cache;
        _log = log;
    }

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

        _log.LogInformation(
            "Table export ticket issued: token {Token}…, user '{UserId}', company '{CompanyId}', {Rows} rows -> {Path}",
            Head(ticket.Token), userId, companyId, rows, filePath);

        return ticket;
    }

    public TableExportTicket? Resolve(string token, string userId, string companyId)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userId))
            return null;

        if (!_cache.TryGetValue(CacheKey(token), out TableExportTicket? ticket) || ticket is null)
        {
            _log.LogWarning(
                "Table export refused: no ticket for token {Token}… (already collected, expired, or " +
                "issued by a different process). Collector was user '{UserId}', company '{CompanyId}'.",
                Head(token), userId, companyId);
            return null;
        }

        // Both identities must match the ticket; see Resolve's contract for why nothing is consumed here.
        if (!string.Equals(ticket.UserId, userId, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "Table export refused: token {Token}… was issued to user '{Issued}' but collected by " +
                "'{Collector}'.", Head(token), ticket.UserId, userId);
            return null;
        }

        if (!string.Equals(ticket.CompanyId, companyId, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "Table export refused: token {Token}… was issued for company '{Issued}' but collected " +
                "with '{Collector}'.", Head(token), ticket.CompanyId, companyId);
            return null;
        }

        // The cache can outlive the file if the folder was cleared underneath us.
        if (!File.Exists(ticket.FilePath))
        {
            _log.LogWarning("Table export refused: token {Token}… names a file that is gone ({Path}).",
                Head(token), ticket.FilePath);
            return null;
        }

        return ticket;
    }

    public void Complete(TableExportTicket ticket)
    {
        // Remove first: the eviction callback skips EvictionReason.Removed precisely so it does not race
        // the delete below.
        _cache.Remove(CacheKey(ticket.Token));
        Delete(ticket.FilePath);
    }

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

    /// <summary>A token prefix for logs — enough to correlate an issue with a redeem, not enough to replay.</summary>
    private static string Head(string token) =>
        string.IsNullOrEmpty(token) ? "(none)" : token[..Math.Min(8, token.Length)];

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

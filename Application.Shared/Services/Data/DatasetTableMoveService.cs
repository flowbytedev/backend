using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Shared.Services.Data;

public class DatasetTableMoveService : IDatasetTableMoveService
{
    private readonly ApplicationDbContext _context;
    private readonly UserManagementDbContext _userContext;
    private readonly IDuckdbService _duckdbService;
    private readonly ITableMovedNotificationService _notifier;
    private readonly ILogger<DatasetTableMoveService> _logger;

    public DatasetTableMoveService(
        ApplicationDbContext context,
        UserManagementDbContext userContext,
        IDuckdbService duckdbService,
        ITableMovedNotificationService notifier,
        ILogger<DatasetTableMoveService> logger)
    {
        _context = context;
        _userContext = userContext;
        _duckdbService = duckdbService;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<MoveTableOutcome> MoveTableAsync(
        string companyId, string sourceDatasetId, string tableName, string targetDatasetId,
        string movedByUserId, string movedByName, CancellationToken ct = default)
    {
        var outcome = new MoveTableOutcome();

        if (string.Equals(sourceDatasetId, targetDatasetId, StringComparison.OrdinalIgnoreCase))
        {
            outcome.Error = "Source and destination datasets must be different.";
            return outcome;
        }

        var sourceDataset = await _context.Dataset.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == sourceDatasetId && d.CompanyId == companyId, ct);
        var targetDataset = await _context.Dataset.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == targetDatasetId && d.CompanyId == companyId, ct);
        if (sourceDataset == null || targetDataset == null)
        {
            outcome.Error = "Source or destination dataset was not found.";
            return outcome;
        }

        // Data move first — the highest-value, hardest-to-redo step. Sharing/notify run only after a
        // confirmed success and never roll it back on their own failure.
        var moveResult = await _duckdbService.MoveTableAsync(sourceDatasetId, tableName, targetDatasetId, ct);
        if (!moveResult.Success)
        {
            outcome.Error = moveResult.Error;
            return outcome;
        }

        outcome.Success = true;

        var affectedUserIds = new List<string>();
        try
        {
            affectedUserIds = await RepointSharingAsync(sourceDatasetId, targetDatasetId, tableName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[TableMove] Table {TableName} moved from {SourceDatasetId} to {TargetDatasetId}, but re-pointing sharing failed.",
                tableName, sourceDatasetId, targetDatasetId);
        }

        if (affectedUserIds.Count > 0)
        {
            try
            {
                var recipients = await _userContext.ApplicationUser
                    .Where(u => affectedUserIds.Contains(u.Id) && u.Email != null)
                    .Select(u => new { u.UserName, u.Email })
                    .ToListAsync(ct);

                foreach (var r in recipients)
                {
                    await _notifier.SendTableMovedNotificationAsync(
                        recipientEmail: r.Email!,
                        recipientName: r.UserName,
                        movedByName: movedByName,
                        tableName: tableName,
                        oldDatasetName: sourceDataset.Name ?? sourceDatasetId,
                        newDatasetId: targetDatasetId,
                        newDatasetName: targetDataset.Name ?? targetDatasetId,
                        companyId: companyId);
                }

                outcome.UsersNotified = recipients.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TableMove] Failed to notify users after moving table {TableName}.", tableName);
            }
        }

        return outcome;
    }

    // Re-points DatasetUserTable rows (the table-scoped shares — exactly "the shared users") from source
    // to target. A DatasetUserTable row is inert without a base DatasetUser grant on that dataset, so each
    // affected user gets one created on the target at their source access level if they don't already have
    // one there (an existing target-side grant is left untouched — no silent escalation). Returns the
    // affected user ids for notification.
    private async Task<List<string>> RepointSharingAsync(string sourceDatasetId, string targetDatasetId, string tableName, CancellationToken ct)
    {
        var scopedRows = await _context.DatasetUserTable
            .Where(t => t.DatasetId == sourceDatasetId && t.TableName == tableName)
            .ToListAsync(ct);

        if (scopedRows.Count == 0)
            return new List<string>();

        var userIds = scopedRows.Select(r => r.UserId).Distinct().ToList();

        var sourceGrantTypes = await _context.DatasetUser
            .Where(u => u.DatasetId == sourceDatasetId && userIds.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Type, ct);

        var targetGrantUserIds = (await _context.DatasetUser
            .Where(u => u.DatasetId == targetDatasetId && userIds.Contains(u.UserId))
            .Select(u => u.UserId)
            .ToListAsync(ct)).ToHashSet();

        var existingTargetScopeUserIds = (await _context.DatasetUserTable
            .Where(t => t.DatasetId == targetDatasetId && t.TableName == tableName && userIds.Contains(t.UserId))
            .Select(t => t.UserId)
            .ToListAsync(ct)).ToHashSet();

        var now = DateTime.UtcNow;
        foreach (var userId in userIds)
        {
            if (!targetGrantUserIds.Contains(userId))
            {
                var type = sourceGrantTypes.TryGetValue(userId, out var t) ? t : DatasetUserType.Viewer;
                _context.DatasetUser.Add(new DatasetUser
                {
                    DatasetId = targetDatasetId,
                    UserId = userId,
                    Type = type,
                    CreatedAt = now
                });
            }

            if (!existingTargetScopeUserIds.Contains(userId))
            {
                _context.DatasetUserTable.Add(new DatasetUserTable
                {
                    DatasetId = targetDatasetId,
                    UserId = userId,
                    TableName = tableName,
                    CreatedAt = now
                });
            }
        }

        _context.DatasetUserTable.RemoveRange(scopedRows);

        await _context.SaveChangesAsync(ct);

        return userIds;
    }
}

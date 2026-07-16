using System.Text.Json;
using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Models.User;
using Application.Shared.Services.Org;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services.Data;

public class DatasetSharingService : IDatasetSharingService
{
    private readonly ApplicationDbContext _context;
    private readonly IUserService _userService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailNotificationService _emailNotificationService;

    public DatasetSharingService(
        ApplicationDbContext context,
        IUserService userService,
        UserManager<ApplicationUser> userManager,
        IEmailNotificationService emailNotificationService)
    {
        _context = context;
        _userService = userService;
        _userManager = userManager;
        _emailNotificationService = emailNotificationService;
    }

    public async Task<List<DatasetUserDto>> GetDatasetUsersAsync(string datasetId)
    {
        // var datasetUsers = await _context.DatasetUser
        //     .Include(du => du.User)
        //     .Where(du => du.DatasetId == datasetId)
        //     .Select(du => new DatasetUserDto
        //     {
        //         UserId = du.UserId,
        //         Email = du.User!.Email!,
        //         UserName = du.User!.UserName!,
        //         Type = du.Type,
        //         CreatedAt = du.CreatedAt
        //     })
        //     .OrderBy(du => du.Email)
        //     .ToListAsync();

        var datasetUsers = await _context.DatasetUser
            .Where(du => du.DatasetId == datasetId)
            .Select(du => new DatasetUserDto
            {
                UserId = du.UserId,
                Type = du.Type,
                CreatedAt = du.CreatedAt
            })
            .ToListAsync();

        // Per-user table scopes (empty = all tables).
        var tableRows = await _context.DatasetUserTable
            .Where(t => t.DatasetId == datasetId)
            .Select(t => new { t.UserId, t.TableName })
            .ToListAsync();
        var tablesByUser = tableRows
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TableName).OrderBy(n => n).ToList());

        // Per-user, per-table column scopes (a table absent here = all columns of that table).
        var columnRows = await _context.DatasetUserColumn
            .Where(c => c.DatasetId == datasetId)
            .Select(c => new { c.UserId, c.TableName, c.ColumnName })
            .ToListAsync();
        var columnsByUser = columnRows
            .GroupBy(c => c.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.TableName)
                      .ToDictionary(tg => tg.Key, tg => tg.Select(x => x.ColumnName).OrderBy(n => n).ToList()));

        // Populate user details using IUserService
        foreach (var du in datasetUsers)
        {
            var user = await _userService.GetUser(du.UserId);
            if (user != null)
            {
                du.Email = user.Email;
                du.UserName = user.UserName;
            }
            if (tablesByUser.TryGetValue(du.UserId, out var tables))
                du.Tables = tables;
            if (columnsByUser.TryGetValue(du.UserId, out var columns))
                du.Columns = columns;
        }

        return datasetUsers;
    }

    public async Task<bool> ShareDatasetAsync(ShareDatasetRequest request, string sharedByUserId)
    {
        try
        {
            // Check if dataset exists
            var dataset = await _context.Dataset.FindAsync(request.DatasetId);
            if (dataset == null)
                return false;

            // Find user by email
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return false;

            // Check if user is already shared with this dataset
            var existingShare = await _context.DatasetUser
                .FirstOrDefaultAsync(du => du.DatasetId == request.DatasetId && du.UserId == user.Id);

            if (existingShare != null)
            {
                // Update existing share
                existingShare.Type = request.UserType;
                existingShare.ModifiedAt = DateTime.UtcNow;
            }
            else
            {
                // Create new share
                var datasetUser = new DatasetUser
                {
                    DatasetId = request.DatasetId,
                    UserId = user.Id,
                    Type = request.UserType,
                    CreatedAt = DateTime.UtcNow
                };

                _context.DatasetUser.Add(datasetUser);
            }

            // Replace the user's table scope. Null/empty Tables = full access (no restriction rows).
            var currentTableRows = await _context.DatasetUserTable
                .Where(t => t.DatasetId == request.DatasetId && t.UserId == user.Id)
                .ToListAsync();
            _context.DatasetUserTable.RemoveRange(currentTableRows);

            var scopedTables = (request.Tables ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var tableName in scopedTables)
            {
                _context.DatasetUserTable.Add(new DatasetUserTable
                {
                    DatasetId = request.DatasetId,
                    UserId = user.Id,
                    TableName = tableName,
                    CreatedAt = DateTime.UtcNow
                });
            }

            // Reconcile the user's per-table column scope. A table absent from request.Columns = all columns.
            // Diff (rather than remove-then-re-add) so identical keys aren't tracked twice in one SaveChanges.
            var desiredColumnKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "table\ncolumn"
            if (request.Columns != null)
            {
                foreach (var (table, columns) in request.Columns)
                {
                    var tbl = table?.Trim();
                    if (string.IsNullOrWhiteSpace(tbl) || columns == null) continue;
                    foreach (var col in columns
                                 .Where(c => !string.IsNullOrWhiteSpace(c))
                                 .Select(c => c.Trim())
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        desiredColumnKeys.Add($"{tbl}\n{col}");
                    }
                }
            }

            var existingColumnRows = await _context.DatasetUserColumn
                .Where(c => c.DatasetId == request.DatasetId && c.UserId == user.Id)
                .ToListAsync();
            var existingColumnKeys = existingColumnRows
                .Select(r => $"{r.TableName}\n{r.ColumnName}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var row in existingColumnRows)
                if (!desiredColumnKeys.Contains($"{row.TableName}\n{row.ColumnName}"))
                    _context.DatasetUserColumn.Remove(row);

            foreach (var key in desiredColumnKeys)
            {
                if (existingColumnKeys.Contains(key)) continue;
                var parts = key.Split('\n', 2);
                _context.DatasetUserColumn.Add(new DatasetUserColumn
                {
                    CompanyId = dataset.CompanyId,
                    UserId = user.Id,
                    DatasetId = request.DatasetId,
                    TableName = parts[0],
                    ColumnName = parts[1],
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            // Get the user who shared the dataset
            var sharedByUser = await _userManager.FindByIdAsync(sharedByUserId);

            // Send email notification (never throws — a mail failure must not fail the share).
            await _emailNotificationService.SendDatasetSharedNotificationAsync(
                request.Email,
                dataset.Id!,
                dataset.Name!,
                dataset.CompanyId,
                sharedByUser?.UserName ?? "Unknown User",
                request.UserType,
                scopedTables);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> GrantTableAccessAsync(GrantTableShareRequest request, string sharedByUserId)
    {
        try
        {
            var dataset = await _context.Dataset.FindAsync(request.DatasetId);
            if (dataset == null) return false;

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null) return false;

            var tableName = request.TableName.Trim();
            if (string.IsNullOrWhiteSpace(tableName)) return false;

            var existingShare = await _context.DatasetUser
                .FirstOrDefaultAsync(du => du.DatasetId == request.DatasetId && du.UserId == user.Id);

            bool tableScoped;
            if (existingShare == null)
            {
                // New share → restricted to just this table.
                _context.DatasetUser.Add(new DatasetUser
                {
                    DatasetId = request.DatasetId,
                    UserId = user.Id,
                    Type = request.UserType,
                    CreatedAt = DateTime.UtcNow
                });
                _context.DatasetUserTable.Add(new DatasetUserTable
                {
                    DatasetId = request.DatasetId,
                    UserId = user.Id,
                    TableName = tableName,
                    CreatedAt = DateTime.UtcNow
                });
                tableScoped = true;
            }
            else if (existingShare.Type != DatasetUserType.Admin)
            {
                var rows = await _context.DatasetUserTable
                    .Where(t => t.DatasetId == request.DatasetId && t.UserId == user.Id)
                    .ToListAsync();

                // rows.Count == 0 means the user already has full access — don't downgrade them to one table.
                if (rows.Count > 0 && !rows.Any(r => string.Equals(r.TableName, tableName, StringComparison.OrdinalIgnoreCase)))
                {
                    _context.DatasetUserTable.Add(new DatasetUserTable
                    {
                        DatasetId = request.DatasetId,
                        UserId = user.Id,
                        TableName = tableName,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                // Column scope only makes sense for a table-scoped user, not one with full dataset access.
                tableScoped = rows.Count > 0;
            }
            else
            {
                // existingShare.Type == Admin → already full access; nothing to do.
                tableScoped = false;
            }

            // Optional per-column restriction for this table. Columns null = leave column scope untouched;
            // empty = all columns (clears any restriction); non-empty = restrict to exactly those columns.
            // Diff (not remove-then-re-add) so identical keys aren't tracked twice in one SaveChanges.
            if (tableScoped && request.Columns != null)
            {
                var desiredColumns = request.Columns
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var existingColumns = await _context.DatasetUserColumn
                    .Where(c => c.DatasetId == request.DatasetId && c.UserId == user.Id && c.TableName == tableName)
                    .ToListAsync();

                foreach (var row in existingColumns)
                    if (!desiredColumns.Contains(row.ColumnName))
                        _context.DatasetUserColumn.Remove(row);

                var existingColumnNames = existingColumns
                    .Select(r => r.ColumnName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var col in desiredColumns)
                    if (!existingColumnNames.Contains(col))
                        _context.DatasetUserColumn.Add(new DatasetUserColumn
                        {
                            CompanyId = dataset.CompanyId,
                            UserId = user.Id,
                            DatasetId = request.DatasetId,
                            TableName = tableName,
                            ColumnName = col,
                            CreatedAt = DateTime.UtcNow
                        });
            }

            await _context.SaveChangesAsync();

            var sharedByUser = await _userManager.FindByIdAsync(sharedByUserId);
            await _emailNotificationService.SendDatasetSharedNotificationAsync(
                request.Email,
                dataset.Id!,
                dataset.Name!,
                dataset.CompanyId,
                sharedByUser?.UserName ?? "Unknown User",
                request.UserType,
                new List<string> { tableName });

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> UpdateDatasetUserTypeAsync(string datasetId, string userId, DatasetUserType userType)
    {
        try
        {
            var datasetUser = await _context.DatasetUser
                .FirstOrDefaultAsync(du => du.DatasetId == datasetId && du.UserId == userId);

            if (datasetUser == null)
                return false;

            datasetUser.Type = userType;
            datasetUser.ModifiedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> RemoveDatasetUserAsync(string datasetId, string userId)
    {
        try
        {
            var datasetUser = await _context.DatasetUser
                .FirstOrDefaultAsync(du => du.DatasetId == datasetId && du.UserId == userId);

            if (datasetUser == null)
                return false;

            // No cascade deletes in this codebase — remove the user's table + column scopes explicitly first.
            var tableRows = await _context.DatasetUserTable
                .Where(t => t.DatasetId == datasetId && t.UserId == userId)
                .ToListAsync();
            _context.DatasetUserTable.RemoveRange(tableRows);

            var columnRows = await _context.DatasetUserColumn
                .Where(c => c.DatasetId == datasetId && c.UserId == userId)
                .ToListAsync();
            _context.DatasetUserColumn.RemoveRange(columnRows);

            _context.DatasetUser.Remove(datasetUser);
            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> RevokeTableAccessAsync(string datasetId, string userId, string tableName)
    {
        try
        {
            var datasetUser = await _context.DatasetUser
                .FirstOrDefaultAsync(du => du.DatasetId == datasetId && du.UserId == userId);
            if (datasetUser == null)
                return false;

            var tableRows = await _context.DatasetUserTable
                .Where(t => t.DatasetId == datasetId && t.UserId == userId)
                .ToListAsync();

            // Full-access users (no table scope) can't have a single table revoked without enumerating the
            // rest — that must be done from dataset sharing.
            if (tableRows.Count == 0)
                return false;

            var row = tableRows.FirstOrDefault(t => t.TableName == tableName);
            if (row == null)
                return false; // not scoped to this table

            _context.DatasetUserTable.Remove(row);

            // Drop any column scope for the revoked table too.
            var colRows = await _context.DatasetUserColumn
                .Where(c => c.DatasetId == datasetId && c.UserId == userId && c.TableName == tableName)
                .ToListAsync();
            _context.DatasetUserColumn.RemoveRange(colRows);

            // If this was their only scoped table, removing it would leave them with "no scope" = full
            // access, which is wrong — their access existed only for this table, so drop the share entirely.
            if (tableRows.Count == 1)
            {
                var allColRows = await _context.DatasetUserColumn
                    .Where(c => c.DatasetId == datasetId && c.UserId == userId)
                    .ToListAsync();
                _context.DatasetUserColumn.RemoveRange(allColRows);
                _context.DatasetUser.Remove(datasetUser);
            }

            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<List<Dataset>> GetSharedDatasetsAsync(string userId, string companyId)
    {
        var sharedDatasets = await _context.DatasetUser
            .Include(du => du.Dataset)
            .Where(du => du.UserId == userId && du.Dataset!.CompanyId == companyId)
            .Select(du => du.Dataset!)
            .ToListAsync();

        return sharedDatasets;
    }

    public async Task<bool> HasDatasetAccessAsync(string datasetId, string userId, DatasetUserType? requiredType = null)
    {
        var datasetUser = await _context.DatasetUser
            .FirstOrDefaultAsync(du => du.DatasetId == datasetId && du.UserId == userId);

        if (datasetUser == null)
            return false;

        if (requiredType == null)
            return true;

        // Check if user has required permission level
        // Admin (0) > Editor (1) > Viewer (2)
        return (int)datasetUser.Type <= (int)requiredType.Value;
    }

    public async Task<Dictionary<string, int>> GetDatasetShareCountsAsync(IEnumerable<string> datasetIds, CancellationToken ct = default)
    {
        var ids = datasetIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<string, int>();

        var counts = await _context.DatasetUser
            .Where(du => ids.Contains(du.DatasetId))
            .GroupBy(du => du.DatasetId)
            .Select(g => new { DatasetId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(x => x.DatasetId, x => x.Count);
    }

    public async Task<Dictionary<string, int>> GetTableShareCountsAsync(string datasetId, IEnumerable<string> tableNames, CancellationToken ct = default)
    {
        var tables = tableNames.Distinct().ToList();
        var result = tables.ToDictionary(t => t, _ => 0);
        if (tables.Count == 0)
            return result;

        // All users the dataset is shared with, and each user's table scope (if any).
        var sharedUsers = await _context.DatasetUser
            .Where(du => du.DatasetId == datasetId)
            .Select(du => du.UserId)
            .ToListAsync(ct);
        if (sharedUsers.Count == 0)
            return result;

        var scopeRows = await _context.DatasetUserTable
            .Where(t => t.DatasetId == datasetId)
            .Select(t => new { t.UserId, t.TableName })
            .ToListAsync(ct);
        var scopedByUser = scopeRows
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TableName).ToHashSet());

        // A shared user with no scope rows has full access → counts for every table. Scoped users count
        // only for the tables in their scope.
        var fullAccessCount = sharedUsers.Count(u => !scopedByUser.ContainsKey(u));
        foreach (var table in tables)
        {
            var scopedCount = scopedByUser.Count(kv => kv.Value.Contains(table));
            result[table] = fullAccessCount + scopedCount;
        }

        return result;
    }

    public async Task<UserDatasetAccessDto> GetUserDatasetAccessAsync(string datasetId, string userId)
    {
        var dto = new UserDatasetAccessDto();

        var datasetUser = await _context.DatasetUser
            .FirstOrDefaultAsync(du => du.DatasetId == datasetId && du.UserId == userId);
        if (datasetUser == null)
            return dto; // HasAccess = false

        dto.HasAccess = true;
        dto.Type = datasetUser.Type;

        dto.Tables = await _context.DatasetUserTable
            .Where(t => t.DatasetId == datasetId && t.UserId == userId)
            .Select(t => t.TableName)
            .OrderBy(n => n)
            .ToListAsync();

        var columnRows = await _context.DatasetUserColumn
            .Where(c => c.DatasetId == datasetId && c.UserId == userId)
            .Select(c => new { c.TableName, c.ColumnName })
            .ToListAsync();
        dto.Columns = columnRows
            .GroupBy(c => c.TableName)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ColumnName).OrderBy(n => n).ToList());

        var rlsRows = await _context.UserRlsFilter
            .Where(r => r.DatasetId == datasetId && r.UserId == userId)
            .ToListAsync();
        dto.Rls = rlsRows
            .OrderBy(r => r.ColumnName)
            .Select(r => new RlsFilterItem { ColumnName = r.ColumnName, AllowedValues = r.GetAllowedValuesList() })
            .ToList();

        return dto;
    }

    public async Task<bool> SetUserDatasetAccessAsync(string companyId, string datasetId, string userId, SetUserAccessRequest request)
    {
        try
        {
            var dataset = await _context.Dataset.FindAsync(datasetId);
            if (dataset == null) return false;

            var datasetUser = await _context.DatasetUser
                .FirstOrDefaultAsync(du => du.DatasetId == datasetId && du.UserId == userId);

            // Remove-all path: drop the share and every scope row.
            if (request.Remove)
            {
                _context.DatasetUserTable.RemoveRange(await _context.DatasetUserTable
                    .Where(t => t.DatasetId == datasetId && t.UserId == userId).ToListAsync());
                _context.DatasetUserColumn.RemoveRange(await _context.DatasetUserColumn
                    .Where(c => c.DatasetId == datasetId && c.UserId == userId).ToListAsync());
                _context.UserRlsFilter.RemoveRange(await _context.UserRlsFilter
                    .Where(r => r.DatasetId == datasetId && r.UserId == userId).ToListAsync());
                if (datasetUser != null) _context.DatasetUser.Remove(datasetUser);
                await _context.SaveChangesAsync();
                return true;
            }

            // Upsert the dataset-level share.
            if (datasetUser == null)
            {
                datasetUser = new DatasetUser { DatasetId = datasetId, UserId = userId, Type = request.Type, CreatedAt = DateTime.UtcNow };
                _context.DatasetUser.Add(datasetUser);
            }
            else
            {
                datasetUser.Type = request.Type;
                datasetUser.ModifiedAt = DateTime.UtcNow;
            }

            // Reconcile table scope (null/empty = all tables → no rows). Diff to avoid re-tracking keys.
            var desiredTables = (request.Tables ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingTables = await _context.DatasetUserTable
                .Where(t => t.DatasetId == datasetId && t.UserId == userId).ToListAsync();
            foreach (var row in existingTables)
                if (!desiredTables.Contains(row.TableName)) _context.DatasetUserTable.Remove(row);
            var existingTableNames = existingTables.Select(r => r.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in desiredTables)
                if (!existingTableNames.Contains(t))
                    _context.DatasetUserTable.Add(new DatasetUserTable { DatasetId = datasetId, UserId = userId, TableName = t, CreatedAt = DateTime.UtcNow });

            // Reconcile per-table column scope (diff on "table\ncolumn").
            var desiredColumnKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request.Columns != null)
                foreach (var (table, columns) in request.Columns)
                {
                    var tbl = table?.Trim();
                    if (string.IsNullOrWhiteSpace(tbl) || columns == null) continue;
                    foreach (var col in columns.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                        desiredColumnKeys.Add($"{tbl}\n{col}");
                }
            var existingColumns = await _context.DatasetUserColumn
                .Where(c => c.DatasetId == datasetId && c.UserId == userId).ToListAsync();
            foreach (var row in existingColumns)
                if (!desiredColumnKeys.Contains($"{row.TableName}\n{row.ColumnName}")) _context.DatasetUserColumn.Remove(row);
            var existingColumnKeys = existingColumns.Select(r => $"{r.TableName}\n{r.ColumnName}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in desiredColumnKeys)
                if (!existingColumnKeys.Contains(key))
                {
                    var parts = key.Split('\n', 2);
                    _context.DatasetUserColumn.Add(new DatasetUserColumn { CompanyId = companyId, UserId = userId, DatasetId = datasetId, TableName = parts[0], ColumnName = parts[1], CreatedAt = DateTime.UtcNow });
                }

            // Reconcile RLS (upsert by column; remove columns no longer present).
            var desiredRls = (request.Rls ?? new List<RlsFilterItem>())
                .Where(r => !string.IsNullOrWhiteSpace(r.ColumnName))
                .GroupBy(r => r.ColumnName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Last().AllowedValues.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var existingRls = await _context.UserRlsFilter
                .Where(r => r.DatasetId == datasetId && r.UserId == userId).ToListAsync();
            foreach (var row in existingRls)
                if (!desiredRls.ContainsKey(row.ColumnName)) _context.UserRlsFilter.Remove(row);
            foreach (var (col, values) in desiredRls)
            {
                var json = JsonSerializer.Serialize(values);
                var existing = existingRls.FirstOrDefault(r => string.Equals(r.ColumnName, col, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    _context.UserRlsFilter.Add(new UserRlsFilter { CompanyId = companyId, UserId = userId, DatasetId = datasetId, ColumnName = col, AllowedValues = json, CreatedAt = DateTime.UtcNow });
                else { existing.AllowedValues = json; existing.ModifiedAt = DateTime.UtcNow; }
            }

            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

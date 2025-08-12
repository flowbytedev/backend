using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Application.Shared.Services.Data;
public class DatasetService : IDatasetService
{
    private readonly ApplicationDbContext _context;
    private readonly IDuckdbService _duckdbService;
    private readonly DuckdbOption _option;

    public DatasetService(ApplicationDbContext context, IDuckdbService duckdbService, DuckdbOption option)
    {
        _context = context;
        _duckdbService = duckdbService;
        _option = option;
    }

    public async Task<Dataset?> GetDatasetAsync(string id, string userId)
    {
        var dataset = await _context.Dataset
            .Where(d => d.Id == id)
            .Where(d => d.CreatedBy == userId || 
                       _context.DatasetUser.Any(du => du.DatasetId == d.Id && du.UserId == userId))
            .FirstOrDefaultAsync();
            
        return dataset;
    }

    public async Task<List<Dataset>> GetDatasetsAsync(string userId)
    {
        return await _context.Dataset
            .Where(d => d.CreatedBy == userId || 
                       _context.DatasetUser.Any(du => du.DatasetId == d.Id && du.UserId == userId))
            .ToListAsync();
    }

    public async Task<List<Dataset>> GetDatasetsByCompanyAsync(string companyId, string userId)
    {
        return await _context.Dataset
            .Where(d => d.CompanyId == companyId)
            .Where(d => d.CreatedBy == userId || 
                       _context.DatasetUser.Any(du => du.DatasetId == d.Id && du.UserId == userId))
            .ToListAsync();
    }

    public async Task<Dataset?> CreateDatasetAsync(Dataset dataset, string userId)
    {
        dataset.Id = Guid.NewGuid().ToString(); // DuckDB won't auto-gen string Id
        dataset.CreatedBy = userId;
        dataset.CreatedAt = DateTime.UtcNow;
        
        _context.Dataset.Add(dataset);

        // Create an admin entry in DatasetUser for the creator
        var datasetUser = new DatasetUser
        {
            DatasetId = dataset.Id,
            UserId = userId,
            Type = DatasetUserType.Admin,
            CreatedAt = DateTime.UtcNow
        };
        _context.DatasetUser.Add(datasetUser);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            if (await DatasetExists(dataset.Id))
                return null;
            throw;
        }

        var duckdbPath = _option.DuckdbFilePath;

        await _duckdbService.CreateDatabaseAsync(dataset);

        return dataset;
    }

    public async Task<Dataset?> UpdateDatasetAsync(string id, Dataset dataset, string userId)
    {
        if (id != dataset.Id)
            return null;

        // Check if user has access to update this dataset (Admin or Editor permissions)
        var hasAccess = await _context.DatasetUser
            .AnyAsync(du => du.DatasetId == id && du.UserId == userId && 
                           (du.Type == DatasetUserType.Admin || du.Type == DatasetUserType.Editor));

        if (!hasAccess)
            return null;

        dataset.ModifiedAt = DateTime.UtcNow;
        _context.Entry(dataset).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await DatasetExists(id))
                return null;
            throw;
        }

        return dataset;
    }

    public async Task<bool> DeleteDatasetAsync(string id, string userId)
    {
        // Check if user has admin access to delete this dataset
        var hasAdminAccess = await _context.DatasetUser
            .AnyAsync(du => du.DatasetId == id && du.UserId == userId && du.Type == DatasetUserType.Admin);

        if (!hasAdminAccess)
            return false;

        var dataset = await _context.Dataset.FindAsync(id);

        if (dataset == null)
            return false;

        // Remove all dataset user relationships
        var datasetUsers = await _context.DatasetUser
            .Where(du => du.DatasetId == id)
            .ToListAsync();
        _context.DatasetUser.RemoveRange(datasetUsers);

        _context.Dataset.Remove(dataset);

        await _context.SaveChangesAsync();

        var duckdbPath = _option.DuckdbFilePath;

        await _duckdbService.DeleteDatabaseAsync(dataset);

        return true;
    }
    private async Task<bool> DatasetExists(string id)
    {
        return await _context.Dataset.AnyAsync(d => d.Id == id);
    }    public async Task<List<Dataset>> GetDatasetsByIdsAsync(List<string> datasetIds, string companyId, string userId)
    {
        if (!datasetIds.Any())
            return new List<Dataset>();

        return await _context.Dataset
            .Where(d => d.Id != null && datasetIds.Contains(d.Id) && d.CompanyId == companyId)
            .Where(d => d.CreatedBy == userId || 
                       _context.DatasetUser.Any(du => du.DatasetId == d.Id && du.UserId == userId))
            .ToListAsync();
    }

    public async Task<List<Dataset>> SearchDatasetsAsync(string query, string companyId, string userId)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetDatasetsByCompanyAsync(companyId, userId);
        }

        var normalizedQuery = query.ToLowerInvariant();
        
        return await _context.Dataset
            .Where(d => d.CompanyId == companyId && 
                       (d.Name!.ToLower().Contains(normalizedQuery) || 
                        d.Description!.ToLower().Contains(normalizedQuery)))
            .Where(d => d.CreatedBy == userId || 
                       _context.DatasetUser.Any(du => du.DatasetId == d.Id && du.UserId == userId))
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<List<TableSearchResult>> SearchTablesAsync(string query, string companyId, string userId)
    {
        var results = new List<TableSearchResult>();
        
        try
        {
            // Get all datasets for the company that the user has access to
            var datasets = await GetDatasetsByCompanyAsync(companyId, userId);
            
            foreach (var dataset in datasets)
            {
                try
                {
                    // Get tables from each dataset
                    var tables = await _duckdbService.GetTablesAsync(dataset.Id!);
                    
                    foreach (var tableName in tables)
                    {
                        // Check if table name matches the search query
                        if (string.IsNullOrWhiteSpace(query) || 
                            tableName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            dataset.Name!.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                // Get table columns and basic info
                                var columns = await _duckdbService.GetTableColumnsAsync(dataset.Id!, tableName);
                                var rowCount = await _duckdbService.GetTableRowCountAsync(dataset.Id!, tableName);
                                
                                results.Add(new TableSearchResult
                                {
                                    Id = $"{dataset.Id}#{tableName}",
                                    DatasetId = dataset.Id!,
                                    DatasetName = dataset.Name!,
                                    TableName = tableName,
                                    Description = $"Table '{tableName}' in dataset '{dataset.Name}' with {rowCount} rows",
                                    CompanyId = companyId,
                                    RowCount = rowCount,
                                    Columns = columns.Select(c => new TableColumn
                                    {
                                        Name = c.Name,
                                        DataType = c.DataType,
                                        IsNullable = c.IsNullable
                                    }).ToList()
                                });
                            }
                            catch (Exception ex)
                            {
                                // Log error but continue with other tables
                                Console.WriteLine($"Error getting table info for {tableName}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue with other datasets
                    Console.WriteLine($"Error accessing dataset {dataset.Name}: {ex.Message}");
                }
            }
            
            return results.OrderBy(r => r.DatasetName).ThenBy(r => r.TableName).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error searching tables: {ex.Message}");
            return new List<TableSearchResult>();
        }
    }

    public async Task<TableReference?> GetTableWithDataAsync(string datasetId, string tableName, string companyId, string userId, int sampleRows = 10)
    {
        try
        {
            // Verify dataset belongs to company and user has access
            var dataset = await _context.Dataset
                .Where(d => d.Id == datasetId && d.CompanyId == companyId)
                .Where(d => d.CreatedBy == userId || 
                           _context.DatasetUser.Any(du => du.DatasetId == d.Id && du.UserId == userId))
                .FirstOrDefaultAsync();
            
            if (dataset == null)
                return null;

            // Get table columns
            var columns = await _duckdbService.GetTableColumnsAsync(datasetId, tableName);
            
            // Get sample data
            var query = new TableDataQuery
            {
                DatasetId = datasetId,
                TableName = tableName,
                Page = 1,
                PageSize = sampleRows
            };
            
            var sampleData = await _duckdbService.QueryTableDataAsync(query);
            
            return new TableReference
            {
                Id = $"{datasetId}#{tableName}",
                DatasetId = datasetId,
                DatasetName = dataset.Name!,
                TableName = tableName,
                Description = $"Table '{tableName}' in dataset '{dataset.Name}' with {sampleData.TotalRows} rows",
                Columns = columns.Select(c => new TableColumn
                {
                    Name = c.Name,
                    DataType = c.DataType,
                    IsNullable = c.IsNullable
                }).ToList(),
                SampleData = sampleData.Data
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting table with data for {tableName}: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TableReference>> GetTablesByReferencesAsync(List<TableReference> tableReferences, string companyId, string userId, int sampleRows = 10)
    {
        var results = new List<TableReference>();
        
        foreach (var tableRef in tableReferences)
        {
            var fullTableData = await GetTableWithDataAsync(tableRef.DatasetId, tableRef.TableName, companyId, userId, sampleRows);
            if (fullTableData != null)
            {
                results.Add(fullTableData);
            }
        }
        
        return results;
    }

}

using Application.Shared.Models;

namespace Application.Shared.Services;

public interface IClickHouseService
{
    Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(MetricDataSource dataSource, string query);
    Task<bool> TestConnectionAsync(MetricDataSource dataSource);
}

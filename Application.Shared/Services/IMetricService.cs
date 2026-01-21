using Application.Shared.Models;

namespace Application.Shared.Services
{
    public interface IMetricService
    {
        Task<List<Metric>> GetMetrics(string companyId);
        Task<Metric?> GetMetric(int id, string companyId);
        Task<Metric> CreateMetric(Metric metric, string userId);
        Task<Metric?> UpdateMetric(int id, Metric metric, string companyId, string userId);
        Task<bool> DeleteMetric(int id, string companyId);
        Task<List<Metric>> GetMetricsByFunction(string function, string companyId);
        Task<List<Metric>> GetMetricsByPerspective(string perspective, string companyId);
    }
}

using Application.Shared.Models;

namespace Application.Shared.Services
{
    public interface IMetricValueService
    {
        Task<List<MetricValue>> GetMetricValues(int metricId, string companyId);
        Task<List<MetricValue>> GetMetricValuesByPeriod(int metricId, DateTime startDate, DateTime endDate, string companyId);
        Task<MetricValue?> GetMetricValue(int id, string companyId);
        Task<MetricValue> CreateMetricValue(MetricValue metricValue, string userId, string companyId);
        Task<MetricValue?> UpdateMetricValue(int id, MetricValue metricValue, string companyId, string userId);
        Task<bool> DeleteMetricValue(int id, string companyId);
        Task<bool> ValidateMetricValue(int id, string companyId, string userId);
    }
}

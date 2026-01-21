using Application.Shared.Models;

namespace Application.Shared.Services
{
    public interface IMetricTargetService
    {
        Task<List<MetricTarget>> GetMetricTargets(int metricId, string companyId);
        Task<MetricTarget?> GetActiveMetricTarget(int metricId, string companyId);
        Task<MetricTarget?> GetMetricTarget(int id, string companyId);
        Task<MetricTarget> CreateMetricTarget(MetricTarget metricTarget, string userId, string companyId);
        Task<MetricTarget?> UpdateMetricTarget(int id, MetricTarget metricTarget, string companyId, string userId);
        Task<bool> DeleteMetricTarget(int id, string companyId);
        Task<bool> DeactivateMetricTarget(int id, string companyId);
    }
}

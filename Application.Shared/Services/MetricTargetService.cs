using Application.Shared.Data;
using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services
{
    public class MetricTargetService : IMetricTargetService
    {
        private readonly ApplicationDbContext _context;

        public MetricTargetService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<MetricTarget>> GetMetricTargets(int metricId, string companyId)
        {
            return await _context.MetricTargets
                .Include(mt => mt.Metric)
                .Where(mt => mt.MetricId == metricId && mt.Metric!.CompanyId == companyId)
                .OrderByDescending(mt => mt.StartDate)
                .ToListAsync();
        }

        public async Task<MetricTarget?> GetActiveMetricTarget(int metricId, string companyId)
        {
            var currentDate = DateTime.UtcNow;
            
            return await _context.MetricTargets
                .Include(mt => mt.Metric)
                .Where(mt => mt.MetricId == metricId 
                    && mt.Metric!.CompanyId == companyId
                    && mt.IsActive
                    && mt.StartDate <= currentDate
                    && (mt.EndDate == null || mt.EndDate >= currentDate))
                .OrderByDescending(mt => mt.StartDate)
                .FirstOrDefaultAsync();
        }

        public async Task<MetricTarget?> GetMetricTarget(int id, string companyId)
        {
            return await _context.MetricTargets
                .Include(mt => mt.Metric)
                .FirstOrDefaultAsync(mt => mt.Id == id && mt.Metric!.CompanyId == companyId);
        }

        public async Task<MetricTarget> CreateMetricTarget(MetricTarget metricTarget, string userId, string companyId)
        {
            // Verify the metric belongs to the company
            var metric = await _context.Metrics
                .FirstOrDefaultAsync(m => m.Id == metricTarget.MetricId && m.CompanyId == companyId);

            if (metric == null)
            {
                throw new InvalidOperationException("Metric not found or does not belong to the company.");
            }

            metricTarget.CreatedOn = DateTime.UtcNow;
            metricTarget.SetBy = userId;
            metricTarget.IsActive = true;

            _context.MetricTargets.Add(metricTarget);
            await _context.SaveChangesAsync();

            return metricTarget;
        }

        public async Task<MetricTarget?> UpdateMetricTarget(int id, MetricTarget metricTarget, string companyId, string userId)
        {
            var existingTarget = await _context.MetricTargets
                .Include(mt => mt.Metric)
                .FirstOrDefaultAsync(mt => mt.Id == id && mt.Metric!.CompanyId == companyId);

            if (existingTarget == null)
            {
                return null;
            }

            existingTarget.StartDate = metricTarget.StartDate;
            existingTarget.EndDate = metricTarget.EndDate;
            existingTarget.MinTarget = metricTarget.MinTarget;
            existingTarget.MaxTarget = metricTarget.MaxTarget;
            existingTarget.OptimalTarget = metricTarget.OptimalTarget;
            existingTarget.TargetDescription = metricTarget.TargetDescription;
            existingTarget.SetBy = userId;
            existingTarget.IsActive = metricTarget.IsActive;

            await _context.SaveChangesAsync();

            return existingTarget;
        }

        public async Task<bool> DeleteMetricTarget(int id, string companyId)
        {
            var metricTarget = await _context.MetricTargets
                .Include(mt => mt.Metric)
                .FirstOrDefaultAsync(mt => mt.Id == id && mt.Metric!.CompanyId == companyId);

            if (metricTarget == null)
            {
                return false;
            }

            _context.MetricTargets.Remove(metricTarget);
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> DeactivateMetricTarget(int id, string companyId)
        {
            var metricTarget = await _context.MetricTargets
                .Include(mt => mt.Metric)
                .FirstOrDefaultAsync(mt => mt.Id == id && mt.Metric!.CompanyId == companyId);

            if (metricTarget == null)
            {
                return false;
            }

            metricTarget.IsActive = false;
            metricTarget.EndDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return true;
        }
    }
}

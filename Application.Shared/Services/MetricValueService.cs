using Application.Shared.Data;
using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services
{
    public class MetricValueService : IMetricValueService
    {
        private readonly ApplicationDbContext _context;

        public MetricValueService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<MetricValue>> GetMetricValues(int metricId, string companyId)
        {
            return await _context.MetricValues
                .Include(mv => mv.Metric)
                .Where(mv => mv.MetricId == metricId && mv.Metric!.CompanyId == companyId)
                .OrderByDescending(mv => mv.PeriodDate)
                .ToListAsync();
        }

        public async Task<List<MetricValue>> GetMetricValuesByPeriod(int metricId, DateTime startDate, DateTime endDate, string companyId)
        {
            return await _context.MetricValues
                .Include(mv => mv.Metric)
                .Where(mv => mv.MetricId == metricId 
                    && mv.Metric!.CompanyId == companyId
                    && mv.PeriodDate >= startDate 
                    && mv.PeriodDate <= endDate)
                .OrderBy(mv => mv.PeriodDate)
                .ToListAsync();
        }

        public async Task<MetricValue?> GetMetricValue(int id, string companyId)
        {
            return await _context.MetricValues
                .Include(mv => mv.Metric)
                .FirstOrDefaultAsync(mv => mv.Id == id && mv.Metric!.CompanyId == companyId);
        }

        public async Task<MetricValue> CreateMetricValue(MetricValue metricValue, string userId, string companyId)
        {
            // Verify the metric belongs to the company
            var metric = await _context.Metrics
                .FirstOrDefaultAsync(m => m.Id == metricValue.MetricId && m.CompanyId == companyId);

            if (metric == null)
            {
                throw new InvalidOperationException("Metric not found or does not belong to the company.");
            }

            metricValue.CreatedOn = DateTime.UtcNow;
            metricValue.CreatedBy = userId;
            metricValue.IsValidated = false;

            _context.MetricValues.Add(metricValue);
            await _context.SaveChangesAsync();

            return metricValue;
        }

        public async Task<MetricValue?> UpdateMetricValue(int id, MetricValue metricValue, string companyId, string userId)
        {
            var existingValue = await _context.MetricValues
                .Include(mv => mv.Metric)
                .FirstOrDefaultAsync(mv => mv.Id == id && mv.Metric!.CompanyId == companyId);

            if (existingValue == null)
            {
                return null;
            }

            existingValue.PeriodDate = metricValue.PeriodDate;
            existingValue.NumericValue = metricValue.NumericValue;
            existingValue.TextValue = metricValue.TextValue;
            existingValue.Notes = metricValue.Notes;
            existingValue.ModifiedBy = userId;
            existingValue.ModifiedOn = DateTime.UtcNow;
            
            // Reset validation when value is updated
            existingValue.IsValidated = false;
            existingValue.ValidatedBy = null;
            existingValue.ValidatedDate = null;

            await _context.SaveChangesAsync();

            return existingValue;
        }

        public async Task<bool> DeleteMetricValue(int id, string companyId)
        {
            var metricValue = await _context.MetricValues
                .Include(mv => mv.Metric)
                .FirstOrDefaultAsync(mv => mv.Id == id && mv.Metric!.CompanyId == companyId);

            if (metricValue == null)
            {
                return false;
            }

            _context.MetricValues.Remove(metricValue);
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> ValidateMetricValue(int id, string companyId, string userId)
        {
            var metricValue = await _context.MetricValues
                .Include(mv => mv.Metric)
                .FirstOrDefaultAsync(mv => mv.Id == id && mv.Metric!.CompanyId == companyId);

            if (metricValue == null)
            {
                return false;
            }

            metricValue.IsValidated = true;
            metricValue.ValidatedBy = userId;
            metricValue.ValidatedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return true;
        }
    }
}

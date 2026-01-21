using Application.Shared.Data;
using Application.Shared.Enums;
using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Application.Shared.Services
{
    public class MetricService : IMetricService
    {
        private readonly ApplicationDbContext _context;

        public MetricService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<Metric>> GetMetrics(string companyId)
        {
            return await _context.Metrics
                .Include(m => m.Functions)
                .Include(m => m.Owners)
                .Include(m => m.Recipients)
                .Include(m => m.Verifiers)
                .Include(m => m.Dimensions)
                .Where(m => m.CompanyId == companyId && m.IsActive)
                .OrderBy(m => m.KPI)
                .ToListAsync();
        }

        public async Task<Metric?> GetMetric(int id, string companyId)
        {
            return await _context.Metrics
                .Include(m => m.Functions)
                .Include(m => m.Owners)
                .Include(m => m.Recipients)
                .Include(m => m.Verifiers)
                .Include(m => m.Dimensions)
                .FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId && m.IsActive);
        }

        public async Task<Metric> CreateMetric(Metric metric, string userId)
        {
            metric.CreatedOn = DateTime.UtcNow;
            metric.CreatedBy = userId;
            metric.IsActive = true;

            _context.Metrics.Add(metric);
            await _context.SaveChangesAsync();

            return metric;
        }

        public async Task<Metric?> UpdateMetric(int id, Metric metric, string companyId, string userId)
        {
            var existingMetric = await _context.Metrics
                .Include(m => m.Functions)
                .Include(m => m.Owners)
                .Include(m => m.Recipients)
                .Include(m => m.Verifiers)
                .Include(m => m.Dimensions)
                .FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId && m.IsActive);

            if (existingMetric == null)
            {
                return null;
            }

            // Update scalar properties
            existingMetric.ContactEmail = metric.ContactEmail;
            existingMetric.ContactNumber = metric.ContactNumber;
            existingMetric.KeyPerformanceArea = metric.KeyPerformanceArea;
            existingMetric.KPI = metric.KPI;
            existingMetric.Formula = metric.Formula;
            existingMetric.Type = metric.Type;
            existingMetric.Perspective = metric.Perspective;
            existingMetric.KPILevel = metric.KPILevel;
            existingMetric.Target = metric.Target;
            existingMetric.UnintendedConsequences = metric.UnintendedConsequences;
            existingMetric.MitigatingFactors = metric.MitigatingFactors;
            existingMetric.UnitOfMeasure = metric.UnitOfMeasure;
            existingMetric.KPIControls = metric.KPIControls;
            existingMetric.DataCapture = metric.DataCapture;
            existingMetric.DataReporting = metric.DataReporting;
            existingMetric.Polarity = metric.Polarity;
            existingMetric.DataSource = metric.DataSource;
            existingMetric.DataIntegrity = metric.DataIntegrity;
            existingMetric.RevisionDate = metric.RevisionDate;
            existingMetric.DataReady = metric.DataReady;
            existingMetric.Report = metric.Report;
            existingMetric.Comment = metric.Comment;
            existingMetric.ModifiedOn = DateTime.UtcNow;
            existingMetric.ModifiedBy = userId;

            // Update Functions collection
            _context.MetricFunctions.RemoveRange(existingMetric.Functions);
            existingMetric.Functions = metric.Functions.Select(f => new MetricFunction
            {
                Function = f.Function,
                SubFunction = f.SubFunction,
                FunctionHead = f.FunctionHead,
                CompanyId = companyId,
                CreatedBy = userId,
                CreatedOn = DateTime.UtcNow
            }).ToList();

            // Update Owners collection
            _context.MetricOwners.RemoveRange(existingMetric.Owners);
            existingMetric.Owners = metric.Owners.Select(o => new MetricOwner
            {
                OwnerName = o.OwnerName,
                CompanyId = companyId,
                CreatedBy = userId,
                CreatedOn = DateTime.UtcNow
            }).ToList();

            // Update Recipients collection
            _context.MetricRecipients.RemoveRange(existingMetric.Recipients);
            existingMetric.Recipients = metric.Recipients.Select(r => new MetricRecipient
            {
                RecipientName = r.RecipientName,
                CompanyId = companyId,
                CreatedBy = userId,
                CreatedOn = DateTime.UtcNow
            }).ToList();

            // Update Verifiers collection
            _context.MetricVerifiers.RemoveRange(existingMetric.Verifiers);
            existingMetric.Verifiers = metric.Verifiers.Select(v => new MetricVerifier
            {
                VerifierName = v.VerifierName,
                CompanyId = companyId,
                CreatedBy = userId,
                CreatedOn = DateTime.UtcNow
            }).ToList();

            // Update Dimensions collection
            _context.MetricDimensions.RemoveRange(existingMetric.Dimensions);
            existingMetric.Dimensions = metric.Dimensions.Select(d => new MetricDimension
            {
                Name = d.Name,
                Description = d.Description,
                SourceTable = d.SourceTable,
                SourceColumn = d.SourceColumn,
                CompanyId = companyId,
                CreatedBy = userId,
                CreatedOn = DateTime.UtcNow
            }).ToList();

            await _context.SaveChangesAsync();

            return existingMetric;
        }

        public async Task<bool> DeleteMetric(int id, string companyId)
        {
            var metric = await _context.Metrics
                .FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId);

            if (metric == null)
            {
                return false;
            }

            // Soft delete
            metric.IsActive = false;
            metric.ModifiedOn = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<List<Metric>> GetMetricsByFunction(string function, string companyId)
        {
            return await _context.Metrics
                .Include(m => m.Functions)
                .Include(m => m.Owners)
                .Include(m => m.Recipients)
                .Include(m => m.Verifiers)
                .Include(m => m.Dimensions)
                .Where(m => m.CompanyId == companyId && m.Functions.Any(f => f.Function == function) && m.IsActive)
                .OrderBy(m => m.KPI)
                .ToListAsync();
        }

        public async Task<List<Metric>> GetMetricsByPerspective(string perspective, string companyId)
        {
            if (Enum.TryParse<MetricPerspective>(perspective, true, out var perspectiveEnum))
            {
                return await _context.Metrics
                    .Include(m => m.Functions)
                    .Include(m => m.Owners)
                    .Include(m => m.Recipients)
                    .Include(m => m.Verifiers)
                    .Include(m => m.Dimensions)
                    .Where(m => m.CompanyId == companyId && m.Perspective == perspectiveEnum && m.IsActive)
                    .OrderBy(m => m.KPI)
                    .ToListAsync();
            }

            return new List<Metric>();
        }
    }
}

using Application.Shared.Models;
using Application.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Application.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MetricsController : ControllerBase
    {
        private readonly IMetricService _metricService;

        public MetricsController(IMetricService metricService)
        {
            _metricService = metricService;
        }

        // GET: api/Metrics
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Metric>>> GetMetrics([FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metrics = await _metricService.GetMetrics(companyId);
            return Ok(metrics);
        }

        // GET: api/Metrics/5
        [HttpGet("{id}")]
        public async Task<ActionResult<Metric>> GetMetric(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metric = await _metricService.GetMetric(id, companyId);

            if (metric == null)
            {
                return NotFound();
            }

            return Ok(metric);
        }

        // GET: api/Metrics/function/{function}
        [HttpGet("function/{function}")]
        public async Task<ActionResult<IEnumerable<Metric>>> GetMetricsByFunction(string function, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metrics = await _metricService.GetMetricsByFunction(function, companyId);
            return Ok(metrics);
        }

        // GET: api/Metrics/perspective/{perspective}
        [HttpGet("perspective/{perspective}")]
        public async Task<ActionResult<IEnumerable<Metric>>> GetMetricsByPerspective(string perspective, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metrics = await _metricService.GetMetricsByPerspective(perspective, companyId);
            return Ok(metrics);
        }

        // POST: api/Metrics
        [HttpPost]
        public async Task<ActionResult<Metric>> CreateMetric(Metric metric, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            try
            {
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest("User ID is required");
                }

                if (string.IsNullOrEmpty(companyId))
                {
                    return BadRequest("Company ID is required in headers");
                }

                metric.CompanyId = companyId;
                var createdMetric = await _metricService.CreateMetric(metric, userId);

                return CreatedAtAction(nameof(GetMetric), new { id = createdMetric.Id, companyId }, createdMetric);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error creating metric: {ex.Message}");
            }
        }

        // PUT: api/Metrics/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateMetric(int id, Metric metric, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            try
            {
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest("User ID is required");
                }

                if (string.IsNullOrEmpty(companyId))
                {
                    return BadRequest("Company ID is required in headers");
                }

                var updatedMetric = await _metricService.UpdateMetric(id, metric, companyId, userId);

                if (updatedMetric == null)
                {
                    return NotFound();
                }

                return Ok(updatedMetric);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error updating metric: {ex.Message}");
            }
        }

        // DELETE: api/Metrics/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMetric(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var result = await _metricService.DeleteMetric(id, companyId);

            if (!result)
            {
                return NotFound();
            }

            return NoContent();
        }
    }
}

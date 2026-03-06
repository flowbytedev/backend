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

        // POST: api/Metrics/{id}/execute
        [HttpPost("{id}/execute")]
        public async Task<ActionResult<List<Dictionary<string, object?>>>> ExecuteMetricQuery(int id, [FromHeader(Name = "X-Company-Id")] string companyId, [FromBody] List<MetricFilterValue>? filterValues = null)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            try
            {
                var results = await _metricService.ExecuteMetricQuery(id, companyId, filterValues);
                return Ok(results);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error executing query: {ex.Message}");
            }
        }

        #region Data Sources

        // GET: api/Metrics/datasources
        [HttpGet("datasources")]
        public async Task<ActionResult<IEnumerable<MetricDataSource>>> GetDataSources([FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var dataSources = await _metricService.GetDataSources(companyId);
            return Ok(dataSources);
        }

        // GET: api/Metrics/datasources/5
        [HttpGet("datasources/{id}")]
        public async Task<ActionResult<MetricDataSource>> GetDataSource(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var dataSource = await _metricService.GetDataSource(id, companyId);

            if (dataSource == null)
            {
                return NotFound();
            }

            return Ok(dataSource);
        }

        // POST: api/Metrics/datasources
        [HttpPost("datasources")]
        public async Task<ActionResult<MetricDataSource>> CreateDataSource(MetricDataSource dataSource, [FromHeader(Name = "X-Company-Id")] string companyId)
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

                dataSource.CompanyId = companyId;
                var createdDataSource = await _metricService.CreateDataSource(dataSource, userId);

                return CreatedAtAction(nameof(GetDataSource), new { id = createdDataSource.Id, companyId }, createdDataSource);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error creating data source: {ex.Message}");
            }
        }

        // PUT: api/Metrics/datasources/5
        [HttpPut("datasources/{id}")]
        public async Task<IActionResult> UpdateDataSource(int id, MetricDataSource dataSource, [FromHeader(Name = "X-Company-Id")] string companyId)
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

                var updatedDataSource = await _metricService.UpdateDataSource(id, dataSource, companyId, userId);

                if (updatedDataSource == null)
                {
                    return NotFound();
                }

                return Ok(updatedDataSource);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error updating data source: {ex.Message}");
            }
        }

        // DELETE: api/Metrics/datasources/5
        [HttpDelete("datasources/{id}")]
        public async Task<IActionResult> DeleteDataSource(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var result = await _metricService.DeleteDataSource(id, companyId);

            if (!result)
            {
                return BadRequest("Data source not found or is currently in use by metrics");
            }

            return NoContent();
        }

        #endregion
    }
}

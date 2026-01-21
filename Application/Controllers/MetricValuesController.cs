using Application.Shared.Models;
using Application.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Application.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MetricValuesController : ControllerBase
    {
        private readonly IMetricValueService _metricValueService;

        public MetricValuesController(IMetricValueService metricValueService)
        {
            _metricValueService = metricValueService;
        }

        // GET: api/MetricValues/metric/5
        [HttpGet("metric/{metricId}")]
        public async Task<ActionResult<IEnumerable<MetricValue>>> GetMetricValues(int metricId, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metricValues = await _metricValueService.GetMetricValues(metricId, companyId);
            return Ok(metricValues);
        }

        // GET: api/MetricValues/metric/5/period?startDate=2024-01-01&endDate=2024-12-31
        [HttpGet("metric/{metricId}/period")]
        public async Task<ActionResult<IEnumerable<MetricValue>>> GetMetricValuesByPeriod(
            int metricId,
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metricValues = await _metricValueService.GetMetricValuesByPeriod(metricId, startDate, endDate, companyId);
            return Ok(metricValues);
        }

        // GET: api/MetricValues/5
        [HttpGet("{id}")]
        public async Task<ActionResult<MetricValue>> GetMetricValue(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metricValue = await _metricValueService.GetMetricValue(id, companyId);

            if (metricValue == null)
            {
                return NotFound();
            }

            return Ok(metricValue);
        }

        // POST: api/MetricValues
        [HttpPost]
        public async Task<ActionResult<MetricValue>> CreateMetricValue(MetricValue metricValue, [FromHeader(Name = "X-Company-Id")] string companyId)
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

                var createdMetricValue = await _metricValueService.CreateMetricValue(metricValue, userId, companyId);

                return CreatedAtAction(nameof(GetMetricValue), new { id = createdMetricValue.Id, companyId }, createdMetricValue);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error creating metric value: {ex.Message}");
            }
        }

        // PUT: api/MetricValues/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateMetricValue(int id, MetricValue metricValue, [FromHeader(Name = "X-Company-Id")] string companyId)
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

                var updatedMetricValue = await _metricValueService.UpdateMetricValue(id, metricValue, companyId, userId);

                if (updatedMetricValue == null)
                {
                    return NotFound();
                }

                return Ok(updatedMetricValue);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error updating metric value: {ex.Message}");
            }
        }

        // DELETE: api/MetricValues/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMetricValue(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var result = await _metricValueService.DeleteMetricValue(id, companyId);

            if (!result)
            {
                return NotFound();
            }

            return NoContent();
        }

        // POST: api/MetricValues/5/validate
        [HttpPost("{id}/validate")]
        public async Task<IActionResult> ValidateMetricValue(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
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

            var result = await _metricValueService.ValidateMetricValue(id, companyId, userId);

            if (!result)
            {
                return NotFound();
            }

            return Ok(new { message = "Metric value validated successfully" });
        }
    }
}

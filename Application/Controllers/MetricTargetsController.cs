using Application.Shared.Models;
using Application.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Application.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MetricTargetsController : ControllerBase
    {
        private readonly IMetricTargetService _metricTargetService;

        public MetricTargetsController(IMetricTargetService metricTargetService)
        {
            _metricTargetService = metricTargetService;
        }

        // GET: api/MetricTargets/metric/5
        [HttpGet("metric/{metricId}")]
        public async Task<ActionResult<IEnumerable<MetricTarget>>> GetMetricTargets(int metricId, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metricTargets = await _metricTargetService.GetMetricTargets(metricId, companyId);
            return Ok(metricTargets);
        }

        // GET: api/MetricTargets/metric/5/active
        [HttpGet("metric/{metricId}/active")]
        public async Task<ActionResult<MetricTarget>> GetActiveMetricTarget(int metricId, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metricTarget = await _metricTargetService.GetActiveMetricTarget(metricId, companyId);

            if (metricTarget == null)
            {
                return NotFound();
            }

            return Ok(metricTarget);
        }

        // GET: api/MetricTargets/5
        [HttpGet("{id}")]
        public async Task<ActionResult<MetricTarget>> GetMetricTarget(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var metricTarget = await _metricTargetService.GetMetricTarget(id, companyId);

            if (metricTarget == null)
            {
                return NotFound();
            }

            return Ok(metricTarget);
        }

        // POST: api/MetricTargets
        [HttpPost]
        public async Task<ActionResult<MetricTarget>> CreateMetricTarget(MetricTarget metricTarget, [FromHeader(Name = "X-Company-Id")] string companyId)
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

                var createdMetricTarget = await _metricTargetService.CreateMetricTarget(metricTarget, userId, companyId);

                return CreatedAtAction(nameof(GetMetricTarget), new { id = createdMetricTarget.Id, companyId }, createdMetricTarget);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error creating metric target: {ex.Message}");
            }
        }

        // PUT: api/MetricTargets/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateMetricTarget(int id, MetricTarget metricTarget, [FromHeader(Name = "X-Company-Id")] string companyId)
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

                var updatedMetricTarget = await _metricTargetService.UpdateMetricTarget(id, metricTarget, companyId, userId);

                if (updatedMetricTarget == null)
                {
                    return NotFound();
                }

                return Ok(updatedMetricTarget);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error updating metric target: {ex.Message}");
            }
        }

        // DELETE: api/MetricTargets/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMetricTarget(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var result = await _metricTargetService.DeleteMetricTarget(id, companyId);

            if (!result)
            {
                return NotFound();
            }

            return NoContent();
        }

        // POST: api/MetricTargets/5/deactivate
        [HttpPost("{id}/deactivate")]
        public async Task<IActionResult> DeactivateMetricTarget(int id, [FromHeader(Name = "X-Company-Id")] string companyId)
        {
            if (string.IsNullOrEmpty(companyId))
            {
                return BadRequest("Company ID is required in headers");
            }

            var result = await _metricTargetService.DeactivateMetricTarget(id, companyId);

            if (!result)
            {
                return NotFound();
            }

            return Ok(new { message = "Metric target deactivated successfully" });
        }
    }
}

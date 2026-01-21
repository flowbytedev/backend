using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Models.User;
using Microsoft.AspNetCore.Identity;
using Application.Shared.Services.Org;
using System.Security.Claims;

namespace Application.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CompaniesController : ControllerBase
    {
        private readonly UserManagementDbContext _context;
        private readonly ICompanyService _companyService;
        private readonly UserManager<ApplicationUser> _userManager;

        public CompaniesController(UserManagementDbContext context, ICompanyService companyService, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _companyService = companyService;
            _userManager = userManager;
        }
                // GET: api/Companies
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Company>>> GetCompanies()
        {
            // get userId from claim
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                return BadRequest("User ID is required in headers");
            }

            var companies = await _companyService.GetCompanies(userId);

            return Ok(companies);
        }        
        
        // POST: api/Companies
        [HttpPost]
        public async Task<ActionResult<Company>> CreateCompany(Company company)
        {
            try
            {
                // get userId from claim
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest("User ID is required in headers");
                }

                var createdCompany = await _companyService.CreateCompany(company, userId);

                return CreatedAtAction(nameof(GetCompanies), new { id = createdCompany.Id }, createdCompany);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error creating company: {ex.Message}");
            }
        }
    }
}

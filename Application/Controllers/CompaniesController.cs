using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Application.Shared.Data;
using Application.Shared.Models;
using Application.Tenancy;
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

        // "Which companies does this person belong to" comes from identity's API. The writes below
        // still go through ICompanyService and the shared catalog: identity has no API for
        // creating or renaming a company yet, and that is the next coupling to remove.
        private readonly ITenancyDirectory _tenancy;
        private readonly UserManager<ApplicationUser> _userManager;

        public CompaniesController(UserManagementDbContext context, ICompanyService companyService, ITenancyDirectory tenancy, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _companyService = companyService;
            _tenancy = tenancy;
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

            var companies = await _tenancy.GetCompaniesAsync(userId);

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

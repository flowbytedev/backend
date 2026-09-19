using System.Security.Claims;
using Application.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Application.Attributes;

/// <summary>
/// Refuses the request unless the signed-in user is a member of the company named in the
/// <c>X-Company-ID</c> header.
/// </summary>
/// <remarks>
/// <b>The user id comes from the principal, never from the request.</b> This filter used to read a
/// <c>userId</c> HTTP header and trust it as the caller's identity, which meant anyone could act as
/// anyone by changing one header. Worse, the client set it on a shared <c>HttpClient</c>'s
/// <c>DefaultRequestHeaders</c>, so it rode along on every subsequent request from that client.
/// <para>
/// There is deliberately no fallback to the header when the claim is absent. A fallback would be
/// the vulnerability, restored.
/// </para>
/// <para>
/// The company id stays a header: it names which tenant the caller is asking about, which is a
/// request parameter, not a claim. It is only ever honoured after the membership check below.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class ValidateCompanyMembershipAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var companyId = context.HttpContext.Request.Headers["X-Company-ID"].ToString();
        if (string.IsNullOrWhiteSpace(companyId))
        {
            context.Result = new BadRequestObjectResult("The X-Company-ID header is required.");
            return;
        }

        var tenancy = context.HttpContext.RequestServices.GetService<ITenancyDirectory>();
        if (tenancy is null)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status500InternalServerError);
            return;
        }

        if (!await tenancy.IsMemberAsync(userId, companyId, context.HttpContext.RequestAborted))
        {
            // 403, not 401: the caller is authenticated, they simply are not a member. Returning
            // 401 would tell a browser to re-authenticate, which cannot help.
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }
}

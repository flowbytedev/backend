using Application.Shared.Models;

namespace Application.Tenancy;

/// <summary>
/// Which company a request works under, given who the person is and what they asked for.
/// </summary>
/// <remarks>
/// <para>
/// One statement of a rule that guards the tenant boundary, because it is applied in two places —
/// the layout, which draws the switcher, and <c>FleetScope</c>, which sets the tenant every query
/// filter reads. Two copies of a security rule is one copy too many.
/// </para>
/// <para>
/// <b>The requested id selects; it never grants.</b> It can only pick from companies the identity
/// application says this person belongs to, so editing <c>?c=</c> in the address bar reaches
/// nothing new. An unknown id falls back rather than failing, because the common case is a stale
/// bookmark rather than an attack, and the fallback is a company they are entitled to anyway.
/// </para>
/// </remarks>
public static class TenantSelection
{
    /// <summary>
    /// The company to work under, or null when the person belongs to none.
    /// </summary>
    /// <param name="memberships">Companies the identity application lists for this person.</param>
    /// <param name="requestedId">The <c>?c=</c> value, if any.</param>
    public static Company? Choose(IReadOnlyList<Company> memberships, string? requestedId)
    {
        ArgumentNullException.ThrowIfNull(memberships);

        if (!string.IsNullOrEmpty(requestedId))
        {
            var requested = memberships.FirstOrDefault(c => string.Equals(c.Id, requestedId, StringComparison.Ordinal));

            if (requested is not null)
            {
                return requested;
            }
        }

        // Callers order the list, so "the first one" is the same company every time rather than
        // whatever the database happened to return.
        return memberships.FirstOrDefault(c => c.Id is not null);
    }
}

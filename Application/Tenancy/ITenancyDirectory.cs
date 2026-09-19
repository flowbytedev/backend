using Application.Shared.Models;

namespace Application.Tenancy;

/// <summary>
/// Which companies a person may act in.
/// </summary>
/// <remarks>
/// <para>
/// This application owns no memberships and no companies. A person belongs to GMRL because the Flowbyte
/// identity application says so, and this is the one place this application asks.
/// </para>
/// <para>
/// <b>It used to be a database connection.</b> It had its own <c>company</c>,
/// <c>company_member</c> and <c>application_user</c> tables, and a context that could be pointed
/// at either their own catalog or identity's — a read-only SQL login against a live application's
/// schema, with a guard in code to stop this application writing to it. Both applications then shared a
/// schema neither could change alone. Identity publishes <c>api/tenancy</c> now, so this application asks
/// over the wire and those three tables are gone.
/// </para>
/// <para>
/// Everything here is a read. There is no write, because membership is granted in identity's own
/// screens by someone with the right to grant it, and nothing in this application has that right.
/// </para>
/// </remarks>
public interface ITenancyDirectory
{
    /// <summary>
    /// The companies this person is an active member of, ordered by name.
    /// </summary>
    /// <remarks>
    /// The order is identity's, and it is stable: the first of these becomes the tenant when no
    /// <c>?c=</c> is given, so "the default company" must be the same company every time.
    /// </remarks>
    /// <returns>An empty list when the person belongs to none, or when identity cannot be reached.</returns>
    Task<IReadOnlyList<Company>> GetCompaniesAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Whether this person may act in this company.
    /// </summary>
    /// <remarks>
    /// The same question as <see cref="GetCompaniesAsync"/>, asked about one company, and answered
    /// from the same list so that the two can never disagree with each other.
    /// </remarks>
    Task<bool> IsMemberAsync(string userId, string companyId, CancellationToken ct = default);
}

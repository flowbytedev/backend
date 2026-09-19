using System.Security.Claims;

namespace Application.Security;

/// <summary>
/// Turns the claims of an identity.gmrlapp.com access token into the principal this application signs in.
/// </summary>
/// <remarks>
/// <para>
/// Identity issues RS256 JWTs with <c>sub</c> (its user id), <c>name</c>, <c>email</c>, zero or
/// more <c>role</c> claims and an optional <c>scope</c>. The mapping is written out rather than
/// left to the JWT handler's inbound claim table, so what this application stores as a user id is a decision
/// made here, once, and testable.
/// </para>
/// <para>
/// <b>The user id is identity's <c>sub</c>, and nothing else.</b> Every <c>company_member</c> row
/// this estate writes keys on it, and it is the same id identity itself uses for its own
/// memberships — so a person is one id across the estate. An Entra object id is not consulted
/// even when present; the day an application here reads one is the day two id spaces start leaking into one
/// table.
/// </para>
/// </remarks>
public static class IdentityClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Email = "email";
    public const string Role = "role";
    public const string Scope = "scope";

    public static ClaimsIdentity Build(IEnumerable<Claim> tokenClaims, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(tokenClaims);

        var claims = tokenClaims.ToList();

        var subject = claims.FirstOrDefault(c => c.Type == Subject)?.Value;

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException("The identity token carries no 'sub' claim; it cannot sign in a user it cannot name.");
        }

        var identity = new ClaimsIdentity(authenticationType, ClaimTypes.Name, ClaimTypes.Role);

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));

        var name = claims.FirstOrDefault(c => c.Type == Name)?.Value;
        var email = claims.FirstOrDefault(c => c.Type == Email)?.Value;

        // A name to greet with, falling back to the email and then the id, so the header never
        // renders blank.
        identity.AddClaim(new Claim(ClaimTypes.Name, FirstNonEmpty(name, email, subject)));

        if (!string.IsNullOrWhiteSpace(email))
        {
            identity.AddClaim(new Claim(ClaimTypes.Email, email));
        }

        foreach (var role in claims.Where(c => c.Type == Role && !string.IsNullOrWhiteSpace(c.Value)).Select(c => c.Value).Distinct(StringComparer.Ordinal))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        foreach (var scope in claims.Where(c => c.Type == Scope && !string.IsNullOrWhiteSpace(c.Value)))
        {
            identity.AddClaim(new Claim(Scope, scope.Value));
        }

        identity.AddClaim(new Claim("idp", "flowbyte-identity"));

        return identity;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}

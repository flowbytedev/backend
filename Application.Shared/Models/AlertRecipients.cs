namespace Application.Shared.Models;

/// <summary>
/// Parsing, validation and canonical storage for a list of alert email addresses typed into a settings box,
/// shared by the settings UI, the API's validation and the sweep that sends the mail — so the three cannot
/// disagree about what "a;b" means.
/// <para>
/// Stored as one <c>;</c>-separated string rather than a child table. There is no query that needs to join
/// on a recipient, and a table would mean a second round trip on a path that already reads
/// <see cref="CompanySettings"/>.
/// </para>
/// </summary>
public static class AlertRecipients
{
    /// <summary>Canonical separator. Matches the one metric filters already use.</summary>
    public const char Separator = ';';

    /// <summary>
    /// Accepted on input as well as <see cref="Separator"/>. Liberal on the way in because a person
    /// pasting addresses out of Outlook or a spreadsheet gets commas or newlines, and rejecting those
    /// would look like the field is broken.
    /// </summary>
    private static readonly char[] AcceptedSeparators = [';', ',', '\n', '\r', ' ', '\t'];

    /// <summary>The addresses in a stored or typed value, trimmed, de-duplicated, order preserved.</summary>
    public static List<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var part in value.Split(AcceptedSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var address = part.Trim();
            if (address.Length > 0 && seen.Add(address)) result.Add(address);
        }

        return result;
    }

    /// <summary>
    /// The form to store: separator-joined, or null when there is nothing left. Null rather than an empty
    /// string so "cleared" and "never set" look the same to every reader, and neither needs special-casing.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var addresses = Parse(value);
        return addresses.Count == 0 ? null : string.Join(Separator, addresses);
    }

    /// <summary>
    /// The first entry that is not a usable address, or null when every entry is fine. Returned rather
    /// than thrown so the API can name the bad one in a message the settings page shows as-is.
    /// <para>
    /// A deliberately shallow check — one <c>@</c>, something either side, a dot in the domain, no
    /// whitespace. Full RFC 5322 is not worth having here: the address is handed to a mail service that
    /// does its own validation, and the only failure this needs to catch is a typo or a pasted name.
    /// </para>
    /// </summary>
    public static string? FirstInvalid(string? value)
    {
        foreach (var address in Parse(value))
        {
            var at = address.IndexOf('@');

            if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1)
                return address;

            var domain = address[(at + 1)..];

            if (!domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.'))
                return address;
        }

        return null;
    }

    /// <summary>
    /// The company's list, falling back to the deployment-wide one when the company has set none. Empty
    /// means send nothing — verdicts are still recorded either way.
    /// </summary>
    public static List<string> Resolve(string? companyValue, IEnumerable<string>? deploymentDefault)
    {
        var configured = Parse(companyValue);
        if (configured.Count > 0) return configured;

        return deploymentDefault is null
            ? []
            : Parse(string.Join(Separator, deploymentDefault));
    }
}

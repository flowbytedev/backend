namespace Application.Shared.Models;

/// <summary>
/// Validation, normalisation and resolution for the folder a pipeline run stages files in — the CSV a
/// database source streams into, the JSON an API source pages into, the copy a blob source downloads.
/// Shared by the settings UI, the API's validation and the run itself, so the three cannot disagree about
/// what a stored value means.
/// <para>
/// The folder matters more than it looks. Every one of those files is a full copy of the data on its way
/// into DuckDB, so a nightly load of tens of millions of rows lands there — and left unset it lands in the
/// service account's temp folder, which on a Windows service is <c>C:\Windows\TEMP</c>: a system volume
/// nobody sized for a data platform, and one an operator cannot move without changing the machine.
/// </para>
/// <para>
/// Null is "not chosen" and reads as <see cref="Default"/>, so existing companies need no backfill.
/// </para>
/// </summary>
public static class PipelineWorkspacePath
{
    /// <summary>Matches the column width. Long enough for a UNC path, short enough to index.</summary>
    public const int MaxLength = 400;

    /// <summary>
    /// What an unset company gets: the OS temp folder, which is what every one of these call sites used
    /// before this setting existed. Keeping the old behaviour as the default is what makes the setting
    /// safe to add to a running deployment.
    /// </summary>
    public static string Default => Path.GetTempPath();

    /// <summary>
    /// The form to store: trimmed, with a trailing separator removed, or null when nothing is left. Null
    /// rather than an empty string so "cleared" and "never set" look identical to every reader.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();

        // Both separators explicitly, never Path.DirectorySeparatorChar — see the note on Validate. In the
        // browser that property is '/', so a Windows path's trailing '\' would survive here and the same
        // folder would normalise to two different strings depending on who asked.
        var withoutTrailing = trimmed.TrimEnd('/', '\\');

        // A root ("C:\", "/") is the one case where the trailing separator is the path, so it is kept.
        if (withoutTrailing.Length > 0 && !withoutTrailing.EndsWith(':')) trimmed = withoutTrailing;

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Why this value cannot be used, or null when it is fine. Returned rather than thrown so the API can
    /// name the problem in a message the settings page shows as-is.
    /// <para>
    /// Deliberately does <b>not</b> require the folder to exist: a scheduler on another machine is what
    /// actually writes there, and refusing a path the web server cannot see would make the setting
    /// unusable in exactly the deployment that needs it most. Existence is handled at run time by
    /// <see cref="Ensure"/>, which creates it and falls back with a log line if it cannot.
    /// </para>
    /// <para>
    /// <b>Every check here is written out by hand rather than delegated to <see cref="Path"/>.</b> That is
    /// not a style choice: this method runs in <em>two</em> runtimes. The settings page validates in the
    /// browser before it sends anything, and Blazor WebAssembly is a Unix-like runtime — so
    /// <c>Path.IsPathRooted</c> there answers "does it start with /", and rejected
    /// <c>D:\flowbyte\work</c> as a relative path while the Windows server it was destined for would have
    /// accepted it. The same applies to <c>Path.GetInvalidPathChars</c> and the separator constants. A
    /// path is validated for where it will be <em>used</em>, which is never "wherever this code happens to
    /// be executing".
    /// </para>
    /// </summary>
    public static string? Validate(string? value)
    {
        var path = Normalize(value);
        if (path is null) return null;   // cleared — back to the default

        if (path.Length > MaxLength)
            return $"That path is {path.Length} characters; the limit is {MaxLength}.";

        if (path.Any(IsForbidden))
            return "That path contains characters that are not allowed in a folder name.";

        if (!IsRooted(path))
            return "Use a full path, for example D:\\flowbyte\\work or \\\\server\\share\\flowbyte.";

        return null;
    }

    /// <summary>
    /// Characters no folder path may contain, on any of the platforms this can be aimed at: the control
    /// characters, the shell redirection set, and the two wildcards — a working folder is one folder, never
    /// a pattern.
    /// <para>
    /// <c>:</c> is deliberately absent. It is legitimate in a Windows drive root, and policing where it may
    /// appear is <see cref="IsRooted"/>'s job.
    /// </para>
    /// </summary>
    private static bool IsForbidden(char c) =>
        char.IsControl(c) || c is '<' or '>' or '"' or '|' or '*' or '?';

    /// <summary>
    /// Whether this is a full path, judged for both platform families rather than for the current runtime.
    /// <para>
    /// A bare drive letter is <b>not</b> rooted: <c>D:</c> and <c>D:work</c> are drive-<em>relative</em> on
    /// Windows, and resolve against whatever that drive's current directory happens to be — which for a
    /// service is not something anybody can predict.
    /// </para>
    /// </summary>
    private static bool IsRooted(string path)
    {
        static bool IsSeparator(char c) => c is '/' or '\\';

        // UNC (\\server\share) or a Unix absolute path (/mnt/data). Also covers a Windows root-relative
        // "\flowbyte", which is rooted in the sense that matters here.
        if (IsSeparator(path[0])) return true;

        // Windows drive root: a letter, a colon, then a separator.
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]);
    }

    /// <summary>The folder to use, without touching the disk. Null or blank means <see cref="Default"/>.</summary>
    public static string Resolve(string? stored) => Normalize(stored) ?? Default;

    /// <summary>
    /// The folder to use, created if it does not exist yet.
    /// <para>
    /// Falls back to <see cref="Default"/> rather than throwing when the configured folder cannot be
    /// created or written to. A misconfigured setting should degrade to the previous behaviour and say so
    /// in the run log; failing every source step in the company over a settings typo is a much worse
    /// outcome than writing to the temp folder for one night.
    /// </para>
    /// </summary>
    /// <param name="onFallback">Called with the reason when the configured folder could not be used.</param>
    public static string Ensure(string? stored, Action<string>? onFallback = null)
    {
        var configured = Normalize(stored);
        if (configured is null) return EnsureDefault();

        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch (Exception ex)
        {
            onFallback?.Invoke(
                $"The pipeline working folder '{configured}' could not be used ({ex.Message}); " +
                $"falling back to '{Default}'.");
            return EnsureDefault();
        }
    }

    private static string EnsureDefault()
    {
        var fallback = Default;
        try { Directory.CreateDirectory(fallback); } catch { /* the OS temp folder always exists */ }
        return fallback;
    }

    /// <summary>
    /// A path for one staging file inside <paramref name="directory"/>. Names carry a <c>pl_</c> prefix and
    /// a GUID so two runs — or two steps of one run — can never collide, and so an operator looking at the
    /// folder can tell a pipeline's leftovers from anything else that writes there.
    /// </summary>
    public static string FileIn(string directory, string prefix, string extension)
    {
        var suffix = string.IsNullOrEmpty(extension) || extension.StartsWith('.')
            ? extension
            : "." + extension;

        return Path.Combine(directory, $"{prefix}_{Guid.NewGuid():N}{suffix}");
    }
}

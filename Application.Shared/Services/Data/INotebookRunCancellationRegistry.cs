namespace Application.Shared.Services.Data;

/// <summary>Tracks the <see cref="CancellationTokenSource"/> behind each in-flight cell/RunAll execution so
/// a separate HTTP request (the user clicking "Cancel") can abort it. Registered as a singleton — there is
/// exactly one of these per process, and only the web app process needs it (interactive runs); the
/// scheduler's recurring runs don't offer cancellation.</summary>
public interface INotebookRunCancellationRegistry
{
    /// <summary>Starts tracking a run under <paramref name="key"/>, linked to the caller's own token (e.g.
    /// HTTP request abort). Returns the combined token to use for the run's actual work.</summary>
    CancellationToken Begin(string key, CancellationToken callerToken);

    /// <summary>Requests cancellation of the run registered under <paramref name="key"/>, if any is active.
    /// Returns false if nothing is currently running under that key.</summary>
    bool Cancel(string key);

    /// <summary>Stops tracking <paramref name="key"/> — call in a finally block once the run completes.</summary>
    void End(string key);
}

using System.Collections.Concurrent;

namespace Application.Shared.Services.Data;

public class NotebookRunCancellationRegistry : INotebookRunCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public CancellationToken Begin(string key, CancellationToken callerToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        // Two runs racing for the same key (shouldn't happen — callers use per-run-instance keys — but stay
        // defensive) — cancel+replace rather than leak the old CTS.
        if (_active.TryRemove(key, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        _active[key] = cts;
        return cts.Token;
    }

    public bool Cancel(string key)
    {
        if (!_active.TryGetValue(key, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public void End(string key)
    {
        if (_active.TryRemove(key, out var cts)) cts.Dispose();
    }
}

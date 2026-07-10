using System.Collections.Concurrent;

namespace CortexBridge.Api.Tmux;

/// <summary>
/// Per-project mutex to enforce "max 1 reply in flight at a time" (spec 03 §3.6).
/// Concurrent POSTs to the same project return 409.
/// </summary>
public class ProjectReplyMutex
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public IDisposable? TryAcquire(string projectId)
    {
        var sem = _locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (!sem.Wait(0)) return null;
        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose()
        {
            _sem?.Release();
            _sem = null;
        }
    }
}

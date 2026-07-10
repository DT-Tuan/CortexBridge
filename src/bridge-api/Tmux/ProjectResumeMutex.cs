namespace CortexBridge.Api.Tmux;

/// <summary>
/// Per-project mutex separate from ProjectReplyMutex. Resume kills + recreates
/// the tmux window — must not race with another resume or a concurrent reply.
/// Spec 04 §"Edge cases" — concurrent resume returns 409.
/// </summary>
public class ProjectResumeMutex
{
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Lease? TryAcquire(string projectId)
    {
        SemaphoreSlim sem;
        lock (_gate)
        {
            if (!_locks.TryGetValue(projectId, out sem!))
            {
                sem = new SemaphoreSlim(1, 1);
                _locks[projectId] = sem;
            }
        }
        return sem.Wait(0) ? new Lease(sem) : null;
    }

    public sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _sem;
        internal Lease(SemaphoreSlim sem) => _sem = sem;
        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
        }
    }
}

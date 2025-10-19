namespace EverTask.Worker;

/// <summary>
/// Use HashSet with lock instead of ConcurrentDictionary for better memory efficiency.
/// Rationale: Blacklist operations are infrequent (only on user cancellation), but IsBlacklisted
/// is called for every task. HashSet with lock provides O(1) performance with lower memory overhead.
/// Lock contention is negligible since Add/Remove are rare operations.
/// </summary>
internal sealed class WorkerBlacklist : IWorkerBlacklist
{
    private readonly HashSet<Guid> _blacklist = new();
    private readonly object _lock = new();

    public void Add(Guid guid)
    {
        lock (_lock)
        {
            _blacklist.Add(guid);
        }
    }

    public bool IsBlacklisted(Guid guid)
    {
        lock (_lock)
        {
            return _blacklist.Contains(guid);
        }
    }

    public void Remove(Guid guid)
    {
        lock (_lock)
        {
            _blacklist.Remove(guid);
        }
    }
}

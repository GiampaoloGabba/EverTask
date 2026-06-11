namespace EverTask.Worker;

/// <summary>
/// Dictionary with lock instead of ConcurrentDictionary for better memory efficiency.
/// Rationale: Blacklist operations are infrequent (only on user cancellation), but IsBlacklisted
/// is called for every task. A locked dictionary provides O(1) performance with lower memory overhead.
/// Lock contention is negligible since Add/Remove are rare operations.
/// </summary>
/// <remarks>
/// Entries are timestamped and swept after <see cref="EntryTtl"/>: cancelling a task whose
/// occurrence is no longer delivered (e.g. it was parked in the scheduler and unscheduled by
/// <c>Cancel</c>) leaves an entry that no consumer will ever <see cref="Remove"/>. Without the
/// sweep those entries would accumulate for the process lifetime.
/// The sweep runs on <see cref="Add"/> only, keeping the hot <see cref="IsBlacklisted"/> path lean.
/// </remarks>
internal sealed class WorkerBlacklist : IWorkerBlacklist
{
    private readonly Dictionary<Guid, DateTimeOffset> _blacklist = new();
    private readonly object _lock = new();

    /// <summary>Entries older than this are swept on Add. Internal for testing purposes.</summary>
    internal TimeSpan EntryTtl { get; set; } = TimeSpan.FromHours(1);

    public void Add(Guid guid)
    {
        lock (_lock)
        {
            Sweep();
            _blacklist[guid] = DateTimeOffset.UtcNow;
        }
    }

    public bool IsBlacklisted(Guid guid)
    {
        lock (_lock)
        {
            return _blacklist.ContainsKey(guid);
        }
    }

    public void Remove(Guid guid)
    {
        lock (_lock)
        {
            _blacklist.Remove(guid);
        }
    }

    private void Sweep()
    {
        if (_blacklist.Count == 0)
            return;

        var cutoff = DateTimeOffset.UtcNow - EntryTtl;

        List<Guid>? expired = null;
        foreach (var entry in _blacklist)
        {
            if (entry.Value < cutoff)
                (expired ??= []).Add(entry.Key);
        }

        if (expired == null)
            return;

        foreach (var id in expired)
            _blacklist.Remove(id);
    }
}

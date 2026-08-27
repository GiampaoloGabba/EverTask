namespace EverTask.Worker;

/// <summary>
/// Ids of cancelled tasks. Entries are timestamped and swept after <see cref="EntryTtl"/>, on
/// <see cref="Add"/> only: cancelling a task that is no longer delivered leaves an entry no consumer will
/// ever <see cref="Remove"/>, and <see cref="IsBlacklisted"/> runs for every task.
/// </summary>
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

using System.Collections.Concurrent;

namespace EverTask.RateLimiting;

/// <summary>
/// Tracks invalidation epochs per task, closing the dequeue→re-park limbo window that
/// <see cref="Scheduler.IScheduler.TryUnschedule(Guid)"/> cannot see: while the rate-limit gate
/// holds a dequeued task (not in the scheduler, not in a worker queue), a concurrent
/// <c>Cancel</c> or same-taskKey immediate re-dispatch has nothing to unschedule. Both call
/// <see cref="Invalidate"/> instead; the gate captures the epoch before parking and re-checks it
/// after <c>Schedule</c> (set-then-check), dropping its now-stale registration if it changed.
/// </summary>
internal interface IGateInvalidationRegistry
{
    /// <summary>
    /// Returns the current invalidation epoch for a task (0 when it was never invalidated
    /// or the entry has lapsed).
    /// </summary>
    long GetEpoch(Guid taskId);

    /// <summary>
    /// Marks the task as invalidated, bumping its epoch. Called by <c>Cancel</c> and by the
    /// immediate same-taskKey re-dispatch path.
    /// </summary>
    void Invalidate(Guid taskId);

    /// <summary>
    /// Returns true if the task was invalidated after the given epoch was captured.
    /// An absent entry means no recent invalidation (entries lapse via TTL), never a change.
    /// </summary>
    bool HasChangedSince(Guid taskId, long epoch);
}

/// <summary>
/// In-memory epoch registry. Entries are rare (one per Cancel / taskKey re-dispatch) and lapse
/// after <see cref="EntryTtl"/>; a sweep runs opportunistically on <see cref="Invalidate"/>.
/// </summary>
internal sealed class GateInvalidationRegistry : IGateInvalidationRegistry
{
    private readonly record struct Entry(long Epoch, long TouchedUtcTicks);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private long _lastSweepUtcTicks;

    /// <summary>Entries older than this are swept. Internal for testing purposes.</summary>
    internal TimeSpan EntryTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Minimum interval between opportunistic sweeps. Internal for testing purposes.</summary>
    internal TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    public long GetEpoch(Guid taskId) =>
        _entries.TryGetValue(taskId, out var entry) ? entry.Epoch : 0;

    public void Invalidate(Guid taskId)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;

        _entries.AddOrUpdate(
            taskId,
            static (_, ticks) => new Entry(1, ticks),
            static (_, entry, ticks) => new Entry(entry.Epoch + 1, ticks),
            nowTicks);

        SweepIfDue(nowTicks);
    }

    public bool HasChangedSince(Guid taskId, long epoch) =>
        _entries.TryGetValue(taskId, out var entry) && entry.Epoch != epoch;

    private void SweepIfDue(long nowTicks)
    {
        var lastSweep = Interlocked.Read(ref _lastSweepUtcTicks);
        if (nowTicks - lastSweep < SweepInterval.Ticks)
            return;

        // Single sweeper at a time; losers skip (the winner does the work)
        if (Interlocked.CompareExchange(ref _lastSweepUtcTicks, nowTicks, lastSweep) != lastSweep)
            return;

        var cutoff = nowTicks - EntryTtl.Ticks;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.TouchedUtcTicks < cutoff)
            {
                // Conditional remove: a concurrent Invalidate refreshing the entry wins
                ((ICollection<KeyValuePair<Guid, Entry>>)_entries).Remove(kvp);
            }
        }
    }
}

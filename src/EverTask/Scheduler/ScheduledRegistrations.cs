using System.Collections.Concurrent;

namespace EverTask.Scheduler;

/// <summary>
/// The parked registrations of one scheduler (or one shard), keyed by
/// <see cref="TaskHandlerExecutor.PersistenceId"/>, together with the heap eviction that replacing one implies.
/// </summary>
/// <remarks>
/// Shared by <see cref="PeriodicTimerScheduler"/> and every shard of <see cref="ShardedScheduler"/>, because
/// what it holds is not a data structure but two RULES that have to be the same in both: latest-wins
/// registration, and the S4 refusal that leaves a registration carrying a newer
/// <see cref="TaskHandlerExecutor.ScheduleVersion"/> alone. Written twice, a fix to either would have landed
/// on whichever scheduler the failing test happened to use and left the other with the old behaviour.
/// </remarks>
/// <param name="queue">
/// The heap the scheduler dequeues from. A replaced registration is evicted from it immediately (CU19), so
/// repeated far-future re-registrations of the same id cannot accumulate orphans held until their due time.
/// </param>
/// <param name="reportSuperseded">
/// How this scheduler logs a refused registration: the id, the version offered and the version parked.
/// </param>
internal sealed class ScheduledRegistrations(
    ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> queue,
    Action<Guid, int, int> reportSuperseded)
{
    private readonly ConcurrentDictionary<Guid, TaskHandlerExecutor> _items = new();

    /// <summary>True while any registration is parked for <paramref name="persistenceId"/>.</summary>
    public bool Contains(Guid persistenceId) => _items.ContainsKey(persistenceId);

    /// <summary>Drops whatever is registered for <paramref name="persistenceId"/>.</summary>
    /// <remarks>
    /// The orphan entry left behind in the heap is discarded by the staleness check at dequeue time.
    /// </remarks>
    public bool Remove(Guid persistenceId) => _items.TryRemove(persistenceId, out _);

    /// <summary>
    /// Drops the registration of <paramref name="persistenceId"/> only while it is still
    /// <paramref name="expected"/>, so a concurrent newer one is preserved.
    /// </summary>
    public bool Remove(Guid persistenceId, TaskHandlerExecutor expected) =>
        _items.TryRemove(new KeyValuePair<Guid, TaskHandlerExecutor>(persistenceId, expected));

    /// <summary>True while <paramref name="item"/> is still the registration its id carries.</summary>
    public bool IsCurrent(TaskHandlerExecutor item) =>
        _items.TryGetValue(item.PersistenceId, out var current) && ReferenceEquals(current, item);

    /// <summary>
    /// Puts <paramref name="item"/> in the registry, evicting whatever it replaces from the heap.
    /// </summary>
    /// <param name="item">The registration being made.</param>
    /// <param name="refuseSuperseded">
    /// True to leave a registration carrying a newer schedule version in place and answer false, instead of
    /// replacing it latest-wins.
    /// </param>
    /// <returns>
    /// False only when a newer registration was found and <paramref name="refuseSuperseded"/> asked for it to
    /// be preserved.
    /// </returns>
    /// <remarks>
    /// The version comparison lives INSIDE the swap: outside it there is always a window in which the newer
    /// registration arrives between the comparison and the write, which is the whole race the conditional
    /// registration exists to close.
    /// </remarks>
    public bool Swap(TaskHandlerExecutor item, bool refuseSuperseded)
    {
        while (true)
        {
            if (!_items.TryGetValue(item.PersistenceId, out var previous))
            {
                if (_items.TryAdd(item.PersistenceId, item))
                    return true;

                continue;
            }

            if (ReferenceEquals(previous, item))
                return true;

            if (refuseSuperseded && previous.ScheduleVersion > item.ScheduleVersion)
            {
                reportSuperseded(item.PersistenceId, item.ScheduleVersion, previous.ScheduleVersion);

                return false;
            }

            if (!_items.TryUpdate(item.PersistenceId, item, previous))
                continue;

            queue.Remove(previous);
            return true;
        }
    }
}

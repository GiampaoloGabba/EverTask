using System.Collections.Concurrent;

namespace EverTask.Scheduler;

/// <summary>
/// The highest schedule version this process has PUBLISHED for a task: the lower bound a delivery that is
/// already in a worker queue is measured against.
/// </summary>
/// <remarks>
/// A delivery already handed to a queue is past the scheduler's reach, so latest-wins registration cannot
/// replace it; this registry is what lets it be dropped instead of executed, at zero cost to storage.
/// The ABSENCE of an entry is not a lower bound of zero: a delivery whose task has no entry is never dropped,
/// which is what keeps an executor recovered at startup from being mistaken for a stale one.
/// Entries go when the schedule ends, never on a timer — an entry that lapsed would let a stale executor
/// through, so a time-to-live would be a correctness change rather than a cleanup.
/// </remarks>
internal sealed class ScheduleVersionRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _versions = new();

    /// <summary>
    /// Publishes <paramref name="version"/> as the version in force for <paramref name="taskId"/>.
    /// </summary>
    /// <remarks>
    /// The maximum wins, never simply the last writer: two reschedules interleaving here could lower the
    /// bound, and a bound that goes backwards lets through exactly the delivery it exists to stop. Called only
    /// AFTER the re-park succeeded, or the old delivery is dropped with nothing to take its place.
    /// </remarks>
    public void Publish(Guid taskId, int version) =>
        _versions.AddOrUpdate(taskId, version, (_, current) => Math.Max(current, version));

    /// <summary>
    /// The published version of <paramref name="taskId"/>, when this process has published one.
    /// </summary>
    public bool TryGetLatest(Guid taskId, out int version) => _versions.TryGetValue(taskId, out version);

    /// <summary>True when this process has published any version for <paramref name="taskId"/>.</summary>
    public bool IsTracked(Guid taskId) => _versions.ContainsKey(taskId);

    /// <summary>Forgets a schedule that will not run again.</summary>
    public void Remove(Guid taskId) => _versions.TryRemove(taskId, out _);
}

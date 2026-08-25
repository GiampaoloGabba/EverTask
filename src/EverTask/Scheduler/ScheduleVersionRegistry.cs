using System.Collections.Concurrent;

namespace EverTask.Scheduler;

/// <summary>
/// The highest schedule version this process has PUBLISHED for a task (S4): the lower bound a delivery that is
/// already in a worker queue is measured against.
/// </summary>
/// <remarks>
/// <para>
/// A reschedule is immediate for occurrences that have not fired yet — the scheduler's registration is replaced
/// latest-wins — but one already handed to a queue is past the scheduler's reach, and by the time it reaches a
/// worker its definition may be two versions old. This registry is what lets that delivery be dropped instead
/// of executed, at zero cost to storage.
/// </para>
/// <para>
/// The ABSENCE of an entry is not a lower bound of zero, it is the absence of one: a delivery whose task has no
/// entry is never dropped. That is what keeps an executor recovered at startup — where nothing has been
/// published yet — from being mistaken for a stale one and thrown away.
/// </para>
/// <para>
/// Entries are removed when the schedule ends (completed, cancelled), never on a timer: the size of the map is
/// the number of live schedules that have been rescheduled in this process, which is bounded by the number of
/// schedules. A time-to-live would be a correctness change, not a cleanup — an entry that lapsed would let a
/// stale executor through.
/// </para>
/// </remarks>
internal sealed class ScheduleVersionRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _versions = new();

    /// <summary>
    /// Publishes <paramref name="version"/> as the version in force for <paramref name="taskId"/>.
    /// </summary>
    /// <remarks>
    /// The maximum wins, never simply the last writer: two reschedules that interleave here would otherwise be
    /// able to lower the bound, and a bound that goes backwards lets through exactly the delivery it exists to
    /// stop. Called only AFTER the re-park succeeded — publishing a version whose executor never reached the
    /// scheduler would drop the old delivery with nothing to take its place.
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

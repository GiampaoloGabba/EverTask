using System.Collections.Concurrent;

namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// How many times in a row each schedule's occurrence provider has failed on this host, and therefore how long
/// the next attempt waits (V4).
/// </summary>
/// <remarks>
/// In memory on purpose, and per host: it is a backoff, not state. A crash loses nothing that matters —
/// the schedule row was never written, so startup recovery asks the provider again and the wait starts over
/// at <see cref="OccurrenceProviderRetryOptions.InitialBackoff"/>.
/// </remarks>
internal sealed class OccurrenceProviderRetryRegistry(EverTaskServiceConfiguration options)
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();

    /// <summary>
    /// Records one more consecutive failure of <paramref name="scheduleId"/> and answers how long it waits and
    /// how many failures that makes.
    /// </summary>
    /// <remarks>
    /// A schedule with no row yet (<see cref="Guid.Empty"/>) is not tracked: the failure belongs to a dispatch
    /// that is about to report it to its caller, and every such dispatch would otherwise share one counter.
    /// </remarks>
    public (TimeSpan Delay, int Failures) RecordFailure(Guid scheduleId)
    {
        if (scheduleId == Guid.Empty)
            return (options.OccurrenceProviderRetry.InitialBackoff, 1);

        var failures = _failures.AddOrUpdate(scheduleId, 1, static (_, previous) => previous + 1);

        return (BackoffFor(failures), failures);
    }

    /// <summary>Forgets <paramref name="scheduleId"/>'s failures: its provider answered.</summary>
    public void RecordSuccess(Guid scheduleId) => Forget(scheduleId);

    /// <summary>
    /// Forgets a schedule that will not ask again: it was cancelled, removed, or its series ended.
    /// </summary>
    /// <remarks>
    /// An answer is the ordinary way an entry goes away, and a schedule that ends while its provider is
    /// healthy has none left to forget. The entries this exists for belong to the schedules that never got
    /// one — cancelled mid-outage, or moved onto a grid with no provider at all — and without it a host would
    /// keep an entry per schedule it ever had. Same lifetime, and the same call sites, as the published
    /// schedule version (S4).
    /// </remarks>
    public void Forget(Guid scheduleId)
    {
        // The empty check is not an optimization: a dispatch with no row shares Guid.Empty with every other
        // one, and none of them is tracked in the first place.
        if (scheduleId != Guid.Empty && !_failures.IsEmpty)
            _failures.TryRemove(scheduleId, out _);
    }

    /// <summary>
    /// The wait after <paramref name="failures"/> consecutive ones: the initial backoff doubled once per
    /// failure, capped.
    /// </summary>
    /// <remarks>
    /// The doubling stops AT the cap rather than being computed and clamped afterwards: an initial backoff
    /// measured in hours reaches the ceiling in a handful of failures, and a schedule whose provider has been
    /// down for a day would otherwise overflow the tick arithmetic on the way to a value the cap discards.
    /// </remarks>
    private TimeSpan BackoffFor(int failures)
    {
        var retry = options.OccurrenceProviderRetry;
        var cap   = retry.MaxBackoff.Ticks;
        var ticks = retry.InitialBackoff.Ticks;

        for (var i = 1; i < failures && ticks < cap; i++)
            ticks = ticks > cap / 2 ? cap : ticks * 2;

        return TimeSpan.FromTicks(Math.Min(ticks, cap));
    }
}

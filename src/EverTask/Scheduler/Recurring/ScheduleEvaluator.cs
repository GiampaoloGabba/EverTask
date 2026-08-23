namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Built-in <see cref="IScheduleEvaluator"/>: a thin, behaviour-neutral wrapper over the pure occurrence math
/// of <see cref="RecurringTask"/>. Every method returns an already-completed <see cref="ValueTask{TResult}"/>,
/// so routing a call site through the seam costs one virtual call and no allocation.
/// </summary>
internal sealed class ScheduleEvaluator : IScheduleEvaluator
{
    /// <summary>
    /// Fallback instance for the few call sites that cannot reach the container (hand-wired executors in
    /// tests, components constructed directly). The DI-registered instance is the one used in production.
    /// </summary>
    internal static readonly ScheduleEvaluator Default = new();

    public ValueTask<NextRunResult> CalculateNextValidRunAsync(
        RecurringTask definition, DateTimeOffset scheduledTime, int currentRun, DateTimeOffset nowUtc,
        DateTimeOffset? referenceTime = null, bool isRecovery = false, bool computeSkippedCount = true,
        CancellationToken ct = default) =>
        new(definition.CalculateNextValidRun(scheduledTime, currentRun, referenceTime, isRecovery,
            computeSkippedCount, nowUtc));

    public ValueTask<DateTimeOffset?> NextAfterAsync(
        RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after, CancellationToken ct = default) =>
        new(definition.NextOccurrenceStrictlyAfter(anchor, after));

    public ValueTask<int> CountMissedAsync(
        RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after, int cap,
        CancellationToken ct = default) =>
        new(definition.CountMissedOccurrences(anchor, after, cap));

    public ValueTask<bool> IsOccurrenceStillCurrentAsync(
        RecurringTask definition, DateTimeOffset occurrence, DateTimeOffset nowUtc,
        CancellationToken ct = default) =>
        new(definition.IsOccurrenceStillCurrent(occurrence, nowUtc));

    public ValueTask<DateTimeOffset?> NextGridOccurrenceAfterAsync(
        RecurringTask definition, DateTimeOffset occurrence, CancellationToken ct = default) =>
        new(definition.NextGridOccurrenceAfter(occurrence));

    /// <summary>
    /// Composed from <see cref="RecurringTask.NextOccurrenceStrictlyAfter"/>, one step at a time, so the due
    /// set is by construction the same grid every other answer here comes from. A materialized list rather
    /// than a stream: <paramref name="cap"/> already bounds it, and keeping the shape of the other members
    /// costs no state machine.
    /// </summary>
    public ValueTask<IReadOnlyList<DateTimeOffset>> EnumerateDueSlotsAsync(
        RecurringTask definition, DateTimeOffset cursor, DateTimeOffset nowUtc, int cap,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);

        // RunUntil is exclusive on the slot, and the walk below only learns about it from the SUCCESSOR:
        // a cursor already at or past the boundary has to be refused here or it would be reported as due.
        if (cap == 0 || cursor > nowUtc || (definition.RunUntil is { } end && cursor >= end))
            return new ValueTask<IReadOnlyList<DateTimeOffset>>(Array.Empty<DateTimeOffset>());

        var slots = new List<DateTimeOffset>();
        var slot  = cursor;

        while (true)
        {
            slots.Add(slot);

            if (slots.Count == cap)
                break;

            // Null means the series ends here (RunUntil); a non-advancing answer is a defensive stop — the
            // same guard the walk inside the primitive keeps, because a slot that never moves would spin.
            if (definition.NextOccurrenceStrictlyAfter(slot, slot) is not { } next || next <= slot || next > nowUtc)
                break;

            slot = next;
        }

        return new ValueTask<IReadOnlyList<DateTimeOffset>>(slots);
    }
}

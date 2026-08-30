using EverTask.Scheduler.Occurrences;

namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Built-in <see cref="IScheduleEvaluator"/>: a thin, behaviour-neutral wrapper over the pure occurrence math
/// of <see cref="RecurringTask"/>, plus the branch that asks an <see cref="INextOccurrenceProvider"/> instead.
/// </summary>
/// <remarks>
/// For a schedule with no provider every method returns an already-completed <see cref="ValueTask{TResult}"/>,
/// so routing a call site through the seam costs one virtual call and no allocation.
/// </remarks>
internal sealed class ScheduleEvaluator(ProviderScheduleGrid? providerGrid = null) : IScheduleEvaluator
{
    /// <summary>
    /// Fallback instance for the few call sites that cannot reach the container (hand-wired executors in
    /// tests, components constructed directly). The DI-registered instance is the one used in production, and
    /// the only one that can answer for a provider-driven schedule.
    /// </summary>
    internal static readonly ScheduleEvaluator Default = new();

    public ValueTask<NextRunResult> CalculateNextValidRunAsync(
        RecurringTask definition, DateTimeOffset scheduledTime, int currentRun, DateTimeOffset nowUtc,
        DateTimeOffset? referenceTime = null, bool isRecovery = false, bool computeSkippedCount = true,
        ScheduleIdentity identity = default, CancellationToken ct = default) =>
        definition.Provider is null
            ? new ValueTask<NextRunResult>(definition.CalculateNextValidRun(scheduledTime, currentRun, referenceTime,
                isRecovery, computeSkippedCount, nowUtc))
            : Provider(definition).CalculateNextValidRunAsync(definition, scheduledTime, currentRun, nowUtc,
                referenceTime, isRecovery, computeSkippedCount, identity, ct);

    public ValueTask<DateTimeOffset?> NextAfterAsync(
        RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after, ScheduleIdentity identity = default,
        CancellationToken ct = default) =>
        definition.Provider is null
            ? new ValueTask<DateTimeOffset?>(definition.NextOccurrenceStrictlyAfter(anchor, after))
            : Provider(definition).NextAfterAsync(definition, anchor, after, identity, ct);

    public ValueTask<int> CountMissedAsync(
        RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after, int cap,
        ScheduleIdentity identity = default, CancellationToken ct = default) =>
        definition.Provider is null
            ? new ValueTask<int>(definition.CountMissedOccurrences(anchor, after, cap))
            : Provider(definition).CountMissedAsync(definition, anchor, after, cap, identity, ct);

    public ValueTask<DateTimeOffset?> NextGridOccurrenceAfterAsync(
        RecurringTask definition, DateTimeOffset occurrence, ScheduleIdentity identity = default,
        CancellationToken ct = default) =>
        definition.Provider is null
            ? new ValueTask<DateTimeOffset?>(definition.NextGridOccurrenceAfter(occurrence))
            : Provider(definition).NextGridOccurrenceAfterAsync(definition, occurrence, identity, ct);

    public ValueTask<DateTimeOffset?> FirstOccurrenceOnOrAfterAsync(
        RecurringTask definition, DateTimeOffset instant, ScheduleIdentity identity = default,
        CancellationToken ct = default) =>
        definition.Provider is null
            ? new ValueTask<DateTimeOffset?>(definition.FirstOccurrenceOnOrAfter(instant))
            : Provider(definition).FirstOccurrenceOnOrAfterAsync(definition, instant, identity, ct);

    public ValueTask<DateTimeOffset?> NormalizeCursorAsync(
        RecurringTask definition, DateTimeOffset cursor, int currentRunCount,
        ScheduleIdentity identity = default, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Exclusions is null || IsPendingFirstRunOverride(definition, cursor, currentRunCount))
            return new ValueTask<DateTimeOffset?>(cursor);

        return FirstOccurrenceOnOrAfterAsync(definition, cursor, identity, ct);
    }

    /// <summary>
    /// Composed from <see cref="NextAfterAsync"/> one step at a time, so the due set comes by construction
    /// from the same grid as every other answer here, with no second implementation of the walk.
    /// </summary>
    public async ValueTask<IReadOnlyList<DateTimeOffset>> EnumerateDueSlotsAsync(
        RecurringTask definition, DateTimeOffset cursor, DateTimeOffset nowUtc, int cap,
        ScheduleIdentity identity = default, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);

        // RunUntil is exclusive on the slot, and the walk below only learns about it from the SUCCESSOR:
        // a cursor already at or past the boundary has to be refused here or it would be reported as due.
        if (cap == 0 || cursor > nowUtc || (definition.RunUntil is { } end && cursor >= end))
            return Array.Empty<DateTimeOffset>();

        var slots = new List<DateTimeOffset>(Math.Min(cap, 256));

        await BoundedOccurrenceWalker
              .WalkAsync(cursor, nowUtc, cap, includeStart: true,
                  slot => NextAfterAsync(definition, slot, slot, identity, ct), slots)
              .ConfigureAwait(false);

        return slots;
    }

    /// <summary>
    /// The provider grid, or the reason there is none.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Default"/> can be without one, and a provider-driven definition cannot be dispatched
    /// through such a component anyway: the dispatcher refuses an unregistered key long before this.
    /// </remarks>
    private ProviderScheduleGrid Provider(RecurringTask definition) =>
        providerGrid
        ?? throw new NotSupportedException(
            $"The schedule takes its occurrences from the provider '{definition.Provider?.Key}', which needs " +
            "the schedule evaluator registered by AddEverTask. This one was built without a container.");

    private static bool IsPendingFirstRunOverride(RecurringTask definition, DateTimeOffset cursor,
                                                  int currentRunCount) =>
        currentRunCount == 0 &&
        (definition.RunNow || definition.InitialDelay != null ||
         definition.SpecificRunTime?.ToUniversalTime() == cursor.ToUniversalTime());
}

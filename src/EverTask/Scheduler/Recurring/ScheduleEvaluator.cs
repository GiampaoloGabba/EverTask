using EverTask.Scheduler.Occurrences;

namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Built-in <see cref="IScheduleEvaluator"/>: a thin, behaviour-neutral wrapper over the pure occurrence math
/// of <see cref="RecurringTask"/>, plus the branch that asks an <see cref="INextOccurrenceProvider"/> instead.
/// </summary>
/// <remarks>
/// For a schedule with no provider every method returns an already-completed <see cref="ValueTask{TResult}"/>,
/// so routing a call site through the seam costs one virtual call and no allocation. The provider branch is
/// the only asynchronous one, and it exists here so that nothing above this line — misfire policies, durable
/// occurrences, skip-forward, the schedule manager — has to know which kind of grid it is working with.
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

    /// <summary>
    /// Composed from <see cref="NextAfterAsync"/>, one step at a time, so the due set is by construction the
    /// same grid every other answer here comes from — the built-in one or the provider's, without a second
    /// implementation of the walk for either. A materialized list rather than a stream:
    /// <paramref name="cap"/> already bounds it, and keeping the shape of the other members costs no state
    /// machine.
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
    /// Only <see cref="Default"/> — the fallback for components built outside the container — can be without
    /// one, and a provider-driven definition cannot be dispatched through such a component anyway: the
    /// dispatcher refuses an unregistered key long before this. The message says which half is missing rather
    /// than letting a null reference say it.
    /// </remarks>
    private ProviderScheduleGrid Provider(RecurringTask definition) =>
        providerGrid
        ?? throw new NotSupportedException(
            $"The schedule takes its occurrences from the provider '{definition.Provider?.Key}', which needs " +
            "the schedule evaluator registered by AddEverTask. This one was built without a container.");
}

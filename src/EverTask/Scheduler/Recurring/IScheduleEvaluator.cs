namespace EverTask.Scheduler.Recurring;

/// <summary>
/// The single seam every component asks about a schedule's occurrence grid: the dispatcher, the worker and
/// the recovery all go through it instead of calling the occurrence math directly.
/// </summary>
/// <remarks>
/// For a built-in schedule the implementation is a synchronous wrapper over the pure primitives on
/// <see cref="RecurringTask"/>. The asynchronous shape is what a schedule whose occurrences come from an
/// <see cref="INextOccurrenceProvider"/> needs — that grid is real I/O — so that every caller works with a
/// provider without knowing one exists.
/// </remarks>
internal interface IScheduleEvaluator
{
    /// <summary>
    /// Next run for <paramref name="definition"/>, realigned past a downtime when the computed occurrence is
    /// already well in the past. Mirrors <c>RecurringTaskExtensions.CalculateNextValidRun</c>.
    /// </summary>
    ValueTask<NextRunResult> CalculateNextValidRunAsync(
        RecurringTask definition, DateTimeOffset scheduledTime, int currentRun, DateTimeOffset nowUtc,
        DateTimeOffset? referenceTime = null, bool isRecovery = false, bool computeSkippedCount = true,
        ScheduleIdentity identity = default, CancellationToken ct = default);

    /// <summary>
    /// First real occurrence strictly after <paramref name="after"/>, anchored on the known occurrence
    /// <paramref name="anchor"/>. Honours the termination bounds: null once the series has ended.
    /// </summary>
    ValueTask<DateTimeOffset?> NextAfterAsync(
        RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after, ScheduleIdentity identity = default,
        CancellationToken ct = default);

    /// <summary>
    /// Number of occurrences in <c>[anchor, after]</c>, bounded at <paramref name="cap"/><c> + 1</c>. Reported
    /// for diagnostics only — it never consumes the run budget.
    /// </summary>
    ValueTask<int> CountMissedAsync(
        RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after, int cap,
        ScheduleIdentity identity = default, CancellationToken ct = default);

    /// <summary>
    /// The natural successor of <paramref name="occurrence"/> on the grid, IGNORING <c>RunUntil</c> and
    /// <c>MaxRuns</c>. The recovery grace window needs this to tell "still the current slot" from "the series
    /// simply ended", which the bounded successor collapses into the same null.
    /// </summary>
    ValueTask<DateTimeOffset?> NextGridOccurrenceAfterAsync(
        RecurringTask definition, DateTimeOffset occurrence, ScheduleIdentity identity = default,
        CancellationToken ct = default);

    /// <summary>
    /// The first occurrence at or ON <paramref name="instant"/> — the one question the grid answers
    /// inclusively, and the cursor a backfilled durable schedule starts from.
    /// </summary>
    ValueTask<DateTimeOffset?> FirstOccurrenceOnOrAfterAsync(
        RecurringTask definition, DateTimeOffset instant, ScheduleIdentity identity = default,
        CancellationToken ct = default);

    /// <summary>
    /// Normalizes a persisted cursor onto the current grid, inclusively. A pending first-run override is
    /// returned unchanged because it is not a grid occurrence.
    /// </summary>
    ValueTask<DateTimeOffset?> NormalizeCursorAsync(
        RecurringTask definition, DateTimeOffset cursor, int currentRunCount,
        ScheduleIdentity identity = default, CancellationToken ct = default);

    /// <summary>
    /// The slots that have already come due at <paramref name="nowUtc"/>, oldest first: the schedule's own
    /// pending slot <paramref name="cursor"/> and every grid occurrence after it that is not later than
    /// <paramref name="nowUtc"/>, stopping at <c>RunUntil</c>.
    /// </summary>
    /// <param name="cap">
    /// Hard upper bound on the number of slots returned — never unbounded, since a one-second grid left behind
    /// by a long downtime has millions of due slots. Ask for one more than the number needed to tell
    /// "exactly n" from "at least n".
    /// </param>
    /// <remarks>
    /// The run budget (<c>MaxRuns</c>) and the misfire policy are NOT applied here: this reports what the grid
    /// owes, and deciding which of those slots become occurrences belongs to the caller.
    /// </remarks>
    ValueTask<IReadOnlyList<DateTimeOffset>> EnumerateDueSlotsAsync(
        RecurringTask definition, DateTimeOffset cursor, DateTimeOffset nowUtc, int cap,
        ScheduleIdentity identity = default, CancellationToken ct = default);
}

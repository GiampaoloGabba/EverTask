using System.Collections.Concurrent;

namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// Turns "where the schedule's cursor is" plus "what time it is" into the list of slots that should become
/// occurrences right now. The whole misfire policy lives here; the materializer only writes what this decides.
/// </summary>
/// <remarks>
/// Every count it takes is BOUNDED: a one-second grid left behind by a three-month downtime owes eight million
/// slots, and the caps are what keep a catch-up decision O(1) on a uniform grid and O(cap) on a calendar one.
/// A cap bounds what one episode may owe, not what one run may write, so a replay measures its backlog once
/// and continues that measurement (<see cref="MeasureBacklogAsync"/>), and a run that may create nothing
/// measures nothing at all.
/// </remarks>
/// <param name="evaluator">The grid.</param>
/// <param name="misfireThreshold">
/// How old a due slot has to be, when it is materialized, before the occurrence it produces says it stands for
/// missed work. The same host-wide <c>SetMisfireThreshold</c> a delivery reports its own lateness against,
/// applied one step earlier: here it decides what the decision records on the row.
/// </param>
internal sealed class DueSlotEnumerator(IScheduleEvaluator evaluator, TimeSpan misfireThreshold)
{
    /// <summary>
    /// Bound on the diagnostic counts (how many slots were dropped) for the grids that have to be WALKED. A
    /// grid that counts by division is never capped — see <see cref="CountDueAsync"/> — and when this bound
    /// bites the count travels as a lower bound and says so, so nobody reads 10,001 as a total. A count with
    /// no cap of its own falls back to it: a number nothing bounds is a number nothing can declare exact.
    /// </summary>
    private const int DiagnosticCountCap = 10_000;

    /// <summary>
    /// That bound, or the far smaller one a grid made of round trips can afford — the shared rule lives with
    /// the provider grid, because the discarded-backlog count of a reschedule takes it too.
    /// </summary>
    private static int DiagnosticCapFor(RecurringTask definition) =>
        ProviderScheduleGrid.DiagnosticCapFor(definition, DiagnosticCountCap);

    /// <summary>
    /// Bound on the bisection that finds where the most recent N slots begin. Each step halves an interval
    /// measured in ticks, so 64 is unreachable for any representable range — it is there so a grid that
    /// answers inconsistently cannot spin.
    /// </summary>
    private const int MaxBisectionSteps = 64;

    /// <summary>
    /// How many times one measurement of a backlog may be CONTINUED before it is taken again from scratch.
    /// </summary>
    /// <remarks>
    /// A provider is not required to answer the same way twice, so a reading states a calendar as it was when
    /// it was read. Continuing it makes a replay cost one walk of its backlog instead of one per occurrence;
    /// re-taking it every so often keeps the cap decision from resting for ever on a calendar somebody has
    /// rewritten underneath.
    /// </remarks>
    private const int MaxContinuedMeasurements = 500;

    /// <summary>
    /// The last backlog measurement of each schedule whose grid is made of round trips, kept so the next run
    /// of the same replay continues it instead of repeating it.
    /// </summary>
    /// <remarks>
    /// At most one entry per durable provider-driven schedule that is behind; the materializer drops it when
    /// it stops materializing that schedule at all (<see cref="Forget"/>).
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, BacklogReading> _backlogs = new();

    /// <summary>
    /// Plans one materialization run.
    /// </summary>
    /// <param name="definition">The schedule.</param>
    /// <param name="cursorUtc">The slot the schedule is currently pointing at (its <c>NextRunUtc</c>).</param>
    /// <param name="nowUtc">The scheduling clock's now.</param>
    /// <param name="currentRunCount">Materializations already spent against <c>MaxRuns</c>.</param>
    /// <param name="activeOccurrences">Occurrences of this schedule that are still alive.</param>
    /// <param name="identity">Which schedule is asking, for a grid that comes from a provider.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DueSlotPlan> PlanAsync(RecurringTask definition, DateTimeOffset cursorUtc,
                                             DateTimeOffset nowUtc, int currentRunCount, int activeOccurrences,
                                             ScheduleIdentity identity = default, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var settings      = definition.Misfire;
        var policy        = definition.MisfirePolicy;
        var maxPending    = settings?.MaxPendingOccurrences ?? 1;
        var capacity      = Math.Max(0, maxPending - activeOccurrences);
        var remainingRuns = definition.MaxRuns is { } max ? Math.Max(0, max - currentRunCount) : int.MaxValue;

        // The run budget is spent, or the cursor already sits at or past the end: either way the series is
        // over and the caller finalizes it. Both checks come first, because a slot outside the bounds is not
        // a slot to grant a grace window to.
        if (remainingRuns == 0 || (definition.RunUntil is { } end && cursorUtc >= end))
            return new DueSlotPlan { NextCursorUtc = null };

        // Nothing due yet: the kick from an occurrence that just ended arrives between two slots all the time.
        if (cursorUtc > nowUtc)
            return new DueSlotPlan { NextCursorUtc = cursorUtc };

        var plan = policy switch
        {
            MisfirePolicy.FireOnce => await PlanFireOnceAsync(definition, settings, cursorUtc, nowUtc, capacity,
                                              identity, ct)
                                          .ConfigureAwait(false),
            MisfirePolicy.CatchUp => await PlanCatchUpAsync(definition, settings!, cursorUtc, nowUtc, capacity,
                                              remainingRuns, identity, ct)
                                         .ConfigureAwait(false),
            _ => await PlanSkipAsync(definition, cursorUtc, nowUtc, capacity, identity, ct).ConfigureAwait(false)
        };

        // Materializing the last allowed occurrence ends the series in the same commit that creates it, so
        // the cursor it leaves is null. The flag says WHY it is null: the caller may write that occurrence at
        // a slot further along the grid, and a budget that is spent stays spent wherever the write lands.
        if (remainingRuns != int.MaxValue && plan.Slots.Count >= remainingRuns)
        {
            plan = plan with
            {
                Slots               = plan.Slots.Take(remainingRuns).ToArray(),
                NextCursorUtc       = null,
                RunBudgetEndsSeries = true
            };
        }

        return plan;
    }

    /// <summary>
    /// The grid slot that follows <paramref name="slot"/>, or <c>null</c> when the schedule's own bounds leave
    /// none after it.
    /// </summary>
    /// <remarks>
    /// What a run needs when it walks past a slot that already has an occurrence: the policy was decided when
    /// the run planned it, so the only question left is where the grid goes next — one step, against the full
    /// plan a re-decision would cost.
    /// </remarks>
    public async ValueTask<DateTimeOffset?> NextSlotAfterAsync(RecurringTask definition, DateTimeOffset slot,
                                                               ScheduleIdentity identity = default,
                                                               CancellationToken ct = default) =>
        await evaluator.NextAfterAsync(definition, slot, slot, identity, ct).ConfigureAwait(false);

    /// <summary>
    /// The legacy policy, in durable clothing: run the slot only while it is still the current one, otherwise
    /// move the cursor past everything that went by.
    /// </summary>
    /// <remarks>
    /// "Still current" is decided on the NATURAL successor, bounds ignored, exactly as the recovery grace
    /// window asks it: the bounded successor is null both while the slot is current and once the series has
    /// ended, and reading that null as the former runs a months-old slot.
    /// </remarks>
    private async Task<DueSlotPlan> PlanSkipAsync(RecurringTask definition, DateTimeOffset cursorUtc,
                                                  DateTimeOffset nowUtc, int capacity, ScheduleIdentity identity,
                                                  CancellationToken ct)
    {
        var successor = await evaluator.NextGridOccurrenceAfterAsync(definition, cursorUtc, identity, ct)
                                       .ConfigureAwait(false);

        if (successor is { } next && next > nowUtc)
        {
            // Still the current slot. With no capacity left the cursor stays put: the slot has not gone stale,
            // it is only waiting for the previous occurrence to end.
            if (capacity == 0)
                return new DueSlotPlan { NextCursorUtc = cursorUtc, StopReason = DueSlotStopReason.WindowFull };

            return new DueSlotPlan
            {
                Slots         = [cursorUtc],
                NextCursorUtc = await evaluator.NextAfterAsync(definition, cursorUtc, cursorUtc, identity, ct)
                                               .ConfigureAwait(false)
            };
        }

        var skipped = await CountDueAsync(definition, cursorUtc, nowUtc, DiagnosticCapFor(definition), identity, ct)
                          .ConfigureAwait(false);

        return new DueSlotPlan
        {
            NextCursorUtc = await evaluator.NextAfterAsync(definition, cursorUtc, nowUtc, identity, ct)
                                           .ConfigureAwait(false),
            Losses        = Loss(SlotLossReason.SkippedByPolicy, cursorUtc, skipped)
        };
    }

    /// <summary>
    /// Collapses the whole run of due slots into ONE occurrence, at the most recent of them. The age window
    /// gates that one slot: when even the newest missed slot is too old, nothing fires at all.
    /// </summary>
    private async Task<DueSlotPlan> PlanFireOnceAsync(RecurringTask definition, MisfireSettings? settings,
                                                      DateTimeOffset cursorUtc, DateTimeOffset nowUtc, int capacity,
                                                      ScheduleIdentity identity, CancellationToken ct)
    {
        // No decision left to take, so the backlog is neither counted nor probed. Every operational retry of
        // a schedule whose window is full comes through here.
        if (capacity == 0 && settings?.MaxAge is null)
            return new DueSlotPlan { NextCursorUtc = cursorUtc, StopReason = DueSlotStopReason.WindowFull };

        var episode = await CountDueAsync(definition, cursorUtc, nowUtc, DiagnosticCapFor(definition), identity, ct)
                          .ConfigureAwait(false);

        // One due slot is the ordinary case and costs no probing: the cursor IS the newest due slot.
        var lastDue = episode.Count <= 1
                          ? cursorUtc
                          : await FindNthSlotFromEndAsync(definition, cursorUtc, nowUtc, 1, identity, ct)
                                .ConfigureAwait(false)
                            ?? cursorUtc;

        if (settings?.MaxAge is { } age && lastDue < AgeCutoff(nowUtc, age))
        {
            return new DueSlotPlan
            {
                NextCursorUtc = await evaluator.NextAfterAsync(definition, cursorUtc, nowUtc, identity, ct)
                                               .ConfigureAwait(false),
                Losses        = Loss(SlotLossReason.MisfireWindowExceeded, cursorUtc, episode)
            };
        }

        if (capacity == 0)
            return new DueSlotPlan { NextCursorUtc = cursorUtc, StopReason = DueSlotStopReason.WindowFull };

        return new DueSlotPlan
        {
            Slots         = [lastDue],
            NextCursorUtc = await evaluator.NextAfterAsync(definition, lastDue, lastDue, identity, ct)
                                           .ConfigureAwait(false),
            Misfire = IsMissed(cursorUtc, nowUtc, episode.Count)
                          ? new OccurrenceMisfire(MisfireKind.FireOnce, cursorUtc, lastDue, episode.Count,
                              episode.IsExact)
                          : null
        };
    }

    /// <summary>
    /// Replays the backlog one occurrence per slot, inside the three limits of <see cref="CatchUpOptions"/>:
    /// the age window drops what is too old, the per-episode cap decides whether the episode runs at all, and
    /// the concurrency budget decides how much of it runs now.
    /// </summary>
    private async Task<DueSlotPlan> PlanCatchUpAsync(RecurringTask definition, MisfireSettings settings,
                                                     DateTimeOffset cursorUtc, DateTimeOffset nowUtc, int capacity,
                                                     int remainingRuns, ScheduleIdentity identity,
                                                     CancellationToken ct)
    {
        var maxOccurrences = settings.MaxOccurrences ?? 1;
        var ageCutoff      = AgeCutoff(nowUtc, settings.MaxAge ?? TimeSpan.Zero);

        var losses = new List<SlotLoss>(2);

        var firstEligible = cursorUtc;

        if (cursorUtc < ageCutoff)
        {
            var windowStart = await FirstSlotOnOrAfterAsync(definition, cursorUtc, ageCutoff, identity, ct)
                                  .ConfigureAwait(false);

            // Nothing inside the window is due (or the grid ended): drop the whole backlog and point the
            // cursor at whatever comes next — the first future slot, or nothing at all.
            if (windowStart is not { } eligibleSlot || eligibleSlot > nowUtc)
            {
                var dropped = await CountDueAsync(definition, cursorUtc, nowUtc, DiagnosticCapFor(definition),
                                      identity, ct)
                                  .ConfigureAwait(false);

                return new DueSlotPlan
                {
                    NextCursorUtc = windowStart,
                    Losses        = Loss(SlotLossReason.MisfireWindowExceeded, cursorUtc, dropped)
                };
            }

            var aged = await CountDueAsync(definition, cursorUtc, eligibleSlot.AddTicks(-1),
                               DiagnosticCapFor(definition), identity, ct)
                           .ConfigureAwait(false);

            if (aged.Count > 0)
                losses.Add(new SlotLoss(SlotLossReason.MisfireWindowExceeded, cursorUtc, aged.Count, aged.IsExact));

            firstEligible = eligibleSlot;
        }

        // Nothing may be created and nothing still to be measured could change that. One step later than the
        // sibling exits, because dropping a slot for its age needs no capacity while materializing one does.
        // Over a provider grid it is the difference between one comparison and a walk of the whole backlog,
        // repeated at every operational retry for as long as the occurrence holding the budget runs.
        if (capacity == 0)
        {
            return new DueSlotPlan
            {
                NextCursorUtc = firstEligible,
                StopReason    = DueSlotStopReason.WindowFull,
                Losses        = losses
            };
        }

        // The count stops one past the cap, which is what tells "at most the cap" from "more than the cap"
        // without enumerating the backlog. Measured ONCE per replay and then continued, instead of re-walking
        // what is left of it per occurrence.
        var measurement = await MeasureBacklogAsync(definition, firstEligible, nowUtc, maxOccurrences, identity, ct)
                              .ConfigureAwait(false);

        var eligible = measurement.Eligible;
        var newest   = measurement.NewestSlotUtc;

        if (eligible.Count > maxOccurrences)
        {
            if (settings.OverflowPolicy == CatchUpOverflowPolicy.Halt)
            {
                // The breaker is about the SIZE of a replay, so it only fires where a replay of that size can
                // happen: a series whose run budget cannot reach the cap creates at most `remainingRuns`
                // occurrences and closes. Halting it instead writes a marker that never releases itself, and
                // an operator would have to resume the schedule just to spend its last run.
                if (remainingRuns > maxOccurrences)
                {
                    return new DueSlotPlan
                    {
                        NextCursorUtc   = cursorUtc,
                        StopReason      = DueSlotStopReason.Halted,
                        DetectedAtLeast = eligible.Count,
                        IsExact         = eligible.IsExact,
                        Losses          = losses
                    };
                }
            }
            else
            {
                // SkipOldest: keep only the most recent cap slots. The start is found by bisecting the INSTANT
                // axis, each probe a bounded forward count, so a grid owing millions of slots costs a few
                // dozen probes instead of an enumeration. Deterministic grids only. Not short-circuited by
                // the run budget: a series down to its last run still owes the newest slot, not the oldest.
                var start = await FindNthSlotFromEndAsync(definition, firstEligible, nowUtc, maxOccurrences,
                                    identity, ct)
                                .ConfigureAwait(false);

                if (start is { } keepFrom && keepFrom > firstEligible)
                {
                    var dropped = await CountDueAsync(definition, firstEligible, keepFrom.AddTicks(-1),
                                          DiagnosticCapFor(definition), identity, ct)
                                      .ConfigureAwait(false);

                    if (dropped.Count > 0)
                    {
                        losses.Add(new SlotLoss(SlotLossReason.CatchUpOverflow, firstEligible, dropped.Count,
                            dropped.IsExact));
                    }

                    firstEligible = keepFrom;
                }

                // The kept window IS the cap, exactly: what the bisection dropped is no longer part of the
                // episode this plan describes.
                eligible = new SlotCount(maxOccurrences, true);
            }
        }

        var affordable = Math.Min(eligible.Count, remainingRuns);
        var take       = Math.Min(affordable, capacity);

        IReadOnlyList<DateTimeOffset> slots = take == 0
            ? Array.Empty<DateTimeOffset>()
            : await evaluator.EnumerateDueSlotsAsync(definition, firstEligible, nowUtc, take, identity, ct)
                             .ConfigureAwait(false);

        var nextCursor = slots.Count > 0
                             ? await evaluator.NextAfterAsync(definition, slots[^1], slots[^1], identity, ct)
                                              .ConfigureAwait(false)
                             : firstEligible;

        // Built only when there ARE rows to stamp it on: a plan whose window is full grants nothing, and
        // closing its range would cost a bisection over the whole backlog at every operational retry.
        OccurrenceMisfire? misfire = null;

        if (slots.Count > 0 && IsMissed(firstEligible, nowUtc, eligible.Count))
        {
            // One slot is its own range: asking for the newest of one costs a bisection to be told what the
            // caller already holds. A continued measurement already knows where its backlog ends.
            var lastEligible = eligible.Count > 1
                                   ? newest ?? await LastEligibleSlotAsync(definition, firstEligible, nowUtc,
                                           identity, ct)
                                       .ConfigureAwait(false)
                                   : firstEligible;

            newest ??= lastEligible;

            misfire = new OccurrenceMisfire(MisfireKind.CatchUp, firstEligible, lastEligible, eligible.Count,
                eligible.IsExact);
        }

        // What this run measured, for the run that picks the replay up from here: the slots it granted are
        // spent, everything else about the reading still stands.
        RememberBacklog(definition, identity, nextCursor, nowUtc, newest, eligible.Count - slots.Count,
            eligible.IsExact, measurement.Generation);

        return new DueSlotPlan
        {
            Slots         = slots,
            NextCursorUtc = nextCursor,
            StopReason    = capacity < affordable ? DueSlotStopReason.WindowFull : DueSlotStopReason.Exhausted,
            Losses        = losses,
            Misfire       = misfire
        };
    }

    /// <summary>
    /// Whether the backlog starting at <paramref name="oldestSlot"/> is MISSED work rather than a schedule
    /// keeping up.
    /// </summary>
    /// <remarks>
    /// Two ways to be missed, and both count: a slot older than <c>MisfireThreshold</c> is missed however
    /// alone it is, and a run of more than one slot is missed however young it is, since the policy is about
    /// to collapse or replay slots nothing ran. The threshold only ever widens the definition. Not private —
    /// the materializer asks it again for the smaller backlog a walked run restates on the row it creates.
    /// </remarks>
    public bool IsMissed(DateTimeOffset oldestSlot, DateTimeOffset nowUtc, int dueCount) =>
        dueCount > 1 || nowUtc - oldestSlot > misfireThreshold;

    /// <summary>The single-run loss list of a plan that dropped slots for exactly one reason, empty when it
    /// dropped none.</summary>
    private static IReadOnlyList<SlotLoss> Loss(SlotLossReason reason, DateTimeOffset fromUtc, SlotCount count) =>
        count.Count > 0 ? [new SlotLoss(reason, fromUtc, count.Count, count.IsExact)] : [];

    /// <summary>
    /// <c>now - age</c>, saturating instead of overflowing.
    /// </summary>
    /// <remarks>
    /// An age window wider than the calendar is legal (<see cref="CatchUpOptions"/> only refuses a
    /// non-positive one) and means "never drop a slot for being old". Computed literally it throws, and it
    /// would throw again at every operational retry, so the series would never materialize anything again.
    /// </remarks>
    private static DateTimeOffset AgeCutoff(DateTimeOffset nowUtc, TimeSpan age) =>
        age >= nowUtc - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : nowUtc - age;

    /// <summary>
    /// The newest slot of the backlog this plan describes — the one that closes the range its
    /// <see cref="OccurrenceMisfire.MissedCount"/> counts.
    /// </summary>
    /// <remarks>
    /// NOT the last slot the plan materializes: the concurrency budget usually truncates that to one, and a
    /// range of one slot carrying a count of five is a record contradicting itself. The caller must already
    /// know there is more than one due slot.
    /// </remarks>
    private async ValueTask<DateTimeOffset> LastEligibleSlotAsync(RecurringTask definition,
                                                                  DateTimeOffset firstEligible,
                                                                  DateTimeOffset nowUtc, ScheduleIdentity identity,
                                                                  CancellationToken ct) =>
        await FindNthSlotFromEndAsync(definition, firstEligible, nowUtc, 1, identity, ct).ConfigureAwait(false)
        ?? firstEligible;

    /// <summary>
    /// The first grid slot at or after <paramref name="instant"/>, walking forward from a known real
    /// occurrence. The grid only answers "strictly after", so the probe sits one tick earlier.
    /// </summary>
    private async ValueTask<DateTimeOffset?> FirstSlotOnOrAfterAsync(RecurringTask definition, DateTimeOffset anchor,
                                                                     DateTimeOffset instant,
                                                                     ScheduleIdentity identity,
                                                                     CancellationToken ct) =>
        instant <= anchor
            ? anchor
            : await evaluator.NextAfterAsync(definition, anchor, instant.AddTicks(-1), identity, ct)
                             .ConfigureAwait(false);

    /// <summary>
    /// How many slots are due in <c>[fromSlot, through]</c>, and whether that is the real total. Zero when the
    /// window is empty.
    /// </summary>
    /// <remarks>
    /// The cap is lifted for a grid that counts by division: it costs one subtraction there, and capping it
    /// would turn "a three-month outage lost 7,900,000 runs" into "it lost 10,001". Where the cap applies the
    /// walk spends the caller's own in full — a second, smaller bound makes every larger answer read as a
    /// total — and <see cref="SlotCount.IsExact"/> keeps a truncated count from being reported as one.
    /// </remarks>
    private async ValueTask<SlotCount> CountDueAsync(RecurringTask definition, DateTimeOffset fromSlot,
                                                     DateTimeOffset through, int cap, ScheduleIdentity identity,
                                                     CancellationToken ct)
    {
        if (through < fromSlot)
            return new SlotCount(0, true);

        var countsExactly = definition.CountsMissedInConstantTime();

        // A walked grid is never asked for an UNCAPPED count: int.MaxValue means "no cap", so the walk keeps
        // a bound of its own and an answer that stops there is a lower bound wearing the shape of a total.
        // MaxOccurrences is an int and may well BE int.MaxValue.
        var walkCap = WalkCapFor(definition, cap);

        var count = await evaluator
                          .CountMissedAsync(definition, fromSlot, through, countsExactly ? int.MaxValue : walkCap,
                              identity, ct)
                          .ConfigureAwait(false);

        return new SlotCount(count, countsExactly || count <= walkCap);
    }

    /// <summary>
    /// The bound a WALK of <paramref name="definition"/>'s grid may spend for a caller asking under
    /// <paramref name="cap"/>: the cap itself, or the walk's own when the ask carries none.
    /// </summary>
    /// <remarks>
    /// One rule, one place: the count and the measurement that continues it have to agree on where "exact"
    /// stops, or a continued reading would call a number exact that a fresh walk reports as a lower bound.
    /// </remarks>
    private static int WalkCapFor(RecurringTask definition, int cap) =>
        cap == int.MaxValue ? DiagnosticCapFor(definition) : cap;

    /// <summary>A diagnostic count together with whether it is the real total or only a lower bound.</summary>
    private readonly record struct SlotCount(int Count, bool IsExact);

    /// <summary>
    /// How many slots the backlog of a catch-up holds, and where it ENDS.
    /// </summary>
    /// <remarks>
    /// The cap bounds what one EPISODE may owe, and "is the backlog bigger than the cap?" costs one question
    /// per slot on a grid made of round trips. Asked again on every run that makes a replay quadratic in its
    /// own backlog, so a run keeps what it measured and the next run CONTINUES it, asking only about the
    /// stretch that came due since — the numbers are the ones a full walk would have produced, since the
    /// count of a chain is the sum of its parts.
    /// <para>
    /// Only where a step is a round trip: a built-in grid counts by division or walks in memory. A reading is
    /// continued only when this run picks up exactly where the last one left the cursor, on the same grid, no
    /// earlier than it was taken — a rewound cursor, a reschedule, another host winning the write, a restart
    /// or a served-slot walk all fall back to the full count — and for at most
    /// <see cref="MaxContinuedMeasurements"/> runs, since a provider may answer differently twice.
    /// </para>
    /// </remarks>
    private async ValueTask<BacklogMeasurement> MeasureBacklogAsync(RecurringTask definition,
                                                                    DateTimeOffset firstEligible,
                                                                    DateTimeOffset nowUtc, int cap,
                                                                    ScheduleIdentity identity, CancellationToken ct)
    {
        if (TryTakeReading(definition, firstEligible, nowUtc, identity, out var reading))
        {
            var walkCap = WalkCapFor(definition, cap);

            // Enough room to tell "at most the cap" from "more than the cap" and not one step further.
            var room  = Math.Max(0, walkCap - reading.Remaining) + 1;
            var since = await WalkForwardAsync(definition, reading.NewestSlotUtc, nowUtc, room, identity, ct)
                            .ConfigureAwait(false);

            var total = reading.Remaining + since.Count;

            // A backlog that no longer owes the slot the cursor names, or whose newest slot moved BEHIND that
            // cursor, is a calendar rewritten under the reading: measure again from scratch. A plain count
            // anchors on the cursor and is never empty, and this must answer the same way or a plan would
            // grant nothing and re-park on the slot it just refused to see.
            if (total > 0 && since.NewestUtc >= firstEligible)
            {
                return new BacklogMeasurement(new SlotCount(Math.Min(total, walkCap + 1), total <= walkCap),
                    since.Bounded ? null : since.NewestUtc, reading.Generation + 1);
            }
        }

        var counted = await CountDueAsync(definition, firstEligible, nowUtc, cap, identity, ct).ConfigureAwait(false);

        // A single due slot IS the newest one, the one thing a full count hands over for free. Anything else
        // costs a bisection, and a plan with no range to close must not pay for one.
        return new BacklogMeasurement(counted, counted.Count == 1 ? firstEligible : null, 0);
    }

    /// <summary>
    /// The reading this run may continue, removed as it is read: a run that does not end with a fresh one
    /// leaves nothing behind, and a reading that does not match is a discontinuity and is dropped with it.
    /// </summary>
    private bool TryTakeReading(RecurringTask definition, DateTimeOffset firstEligible, DateTimeOffset nowUtc,
                                ScheduleIdentity identity, out BacklogReading reading)
    {
        reading = default;

        if (definition.Provider is not { } provider || identity.ScheduleId == Guid.Empty)
            return false;

        if (!_backlogs.TryRemove(identity.ScheduleId, out var stored))
            return false;

        reading = stored;

        return stored.ProviderKey == provider.Key
               && stored.ProviderConfig == provider.Config
               && stored.TimeZoneId == definition.TimeZoneId
               && stored.RunUntil == definition.RunUntil
               && stored.ResumeAtUtc == firstEligible
               && stored.MeasuredThroughUtc <= nowUtc
               && stored.Generation < MaxContinuedMeasurements;
    }

    /// <summary>
    /// Keeps what this run measured for the next one, when there is anything left to continue.
    /// </summary>
    /// <remarks>
    /// Nothing is kept unless the count is a TOTAL: arithmetic on a lower bound produces a number that claims
    /// to be exact and is not. Nor unless the plan knows where the backlog ends and where the cursor goes.
    /// </remarks>
    private void RememberBacklog(RecurringTask definition, ScheduleIdentity identity, DateTimeOffset? resumeAtUtc,
                                 DateTimeOffset nowUtc, DateTimeOffset? newestSlotUtc, int remaining, bool isExact,
                                 int generation)
    {
        if (definition.Provider is not { } provider || identity.ScheduleId == Guid.Empty)
            return;

        if (!isExact || remaining < 0 || resumeAtUtc is not { } resumeAt || newestSlotUtc is not { } newest)
            return;

        _backlogs[identity.ScheduleId] = new BacklogReading(provider.Key, provider.Config, definition.TimeZoneId,
            definition.RunUntil, resumeAt, nowUtc, newest, remaining, generation);
    }

    /// <summary>
    /// Forgets what was measured about a schedule. Called when the materializer stops following one at all —
    /// the series ended, was cancelled, or the row is gone.
    /// </summary>
    public void Forget(Guid scheduleId) => _backlogs.TryRemove(scheduleId, out _);

    /// <summary>
    /// The slots strictly after <paramref name="from"/> and not later than <paramref name="through"/>, walked
    /// one grid step at a time up to <paramref name="limit"/> of them, with the newest one reached.
    /// </summary>
    /// <remarks>
    /// The same walk a bounded count over a provider grid makes, step for step and stopping rule for stopping
    /// rule; this one keeps the slot it stopped on, so the next measurement starts there instead of at the
    /// beginning of the backlog.
    /// </remarks>
    private async ValueTask<(int Count, DateTimeOffset NewestUtc, bool Bounded)> WalkForwardAsync(
        RecurringTask definition, DateTimeOffset from, DateTimeOffset through, int limit, ScheduleIdentity identity,
        CancellationToken ct)
    {
        var walked = await BoundedOccurrenceWalker
                           .WalkAsync(from, through, limit, includeStart: false,
                               slot => evaluator.NextAfterAsync(definition, slot, slot, identity, ct))
                           .ConfigureAwait(false);

        return (walked.Count, walked.NewestUtc, walked.Bounded);
    }

    /// <summary>What one measurement of a backlog answers.</summary>
    /// <param name="Eligible">How many slots are due, under the cap the caller asked with.</param>
    /// <param name="NewestSlotUtc">
    /// The newest of them, when this measurement knows it — the range a misfire closes, without a bisection.
    /// </param>
    /// <param name="Generation">How many times this measurement has been carried forward.</param>
    private readonly record struct BacklogMeasurement(SlotCount Eligible, DateTimeOffset? NewestSlotUtc,
                                                      int Generation);

    /// <summary>
    /// A measurement kept for the next run of the same replay: the grid it was taken on, where that run has to
    /// resume for it to still describe the same backlog, and what is left of it.
    /// </summary>
    /// <param name="ProviderKey">The provider the grid came from.</param>
    /// <param name="ProviderConfig">Its configuration, which is half of what a provider answers about.</param>
    /// <param name="TimeZoneId">The zone the request carries, the other half of the grid's identity.</param>
    /// <param name="RunUntil">The bound that decides where the grid stops answering.</param>
    /// <param name="ResumeAtUtc">The cursor the next run must arrive with: the slot after the last one granted.</param>
    /// <param name="MeasuredThroughUtc">The instant the count ran to.</param>
    /// <param name="NewestSlotUtc">The newest slot at or before that instant.</param>
    /// <param name="Remaining">
    /// The slots still due in <c>[ResumeAtUtc, MeasuredThroughUtc]</c> — an exact total, never a lower bound.
    /// </param>
    /// <param name="Generation">How many times this measurement has been carried forward already.</param>
    private readonly record struct BacklogReading(string ProviderKey, string? ProviderConfig, string? TimeZoneId,
                                                  DateTimeOffset? RunUntil, DateTimeOffset ResumeAtUtc,
                                                  DateTimeOffset MeasuredThroughUtc, DateTimeOffset NewestSlotUtc,
                                                  int Remaining, int Generation);

    /// <summary>
    /// Where the most recent <paramref name="n"/> due slots begin, found by bisecting the instant axis between
    /// <paramref name="firstSlot"/> and <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// The count of slots strictly after an instant decreases by exactly one at each slot as that instant
    /// moves forward, so the boundary where it equals <paramref name="n"/> is the slot wanted and a binary
    /// search finds it. Each probe is a forward count bounded at <paramref name="n"/>. The caller must
    /// already know there are MORE than <paramref name="n"/> due slots.
    /// <para>
    /// It stops at the FIRST probe counting exactly <paramref name="n"/>, which is the answer and not an
    /// approximation: an instant with exactly that many slots after it has the wanted one as its successor,
    /// narrowed to a single tick or not. Running to the tick-level end spends the whole probe budget every
    /// time, and on a provider grid each probe is a pair of round trips.
    /// </para>
    /// <para>
    /// The probe count and the per-probe cost would otherwise multiply on a grid made of round trips, so the
    /// chain the probes re-walk is memoized below and no instant is asked about twice — the whole search then
    /// costs at most one question per slot it looks at. Sound for exactly the grids this policy accepts: a
    /// provider that would answer differently the second time is one <c>SkipOldest</c> refuses at dispatch.
    /// </para>
    /// </remarks>
    private async Task<DateTimeOffset?> FindNthSlotFromEndAsync(RecurringTask definition, DateTimeOffset firstSlot,
                                                                DateTimeOffset nowUtc, int n,
                                                                ScheduleIdentity identity, CancellationToken ct)
    {
        var lo = firstSlot.UtcTicks;
        var hi = nowUtc.UtcTicks;

        // Only where a step costs a round trip: a built-in grid counts by division or walks in memory, and
        // neither is worth remembering.
        var chain = definition.Provider is null ? null : new Dictionary<DateTimeOffset, DateTimeOffset?>();

        for (var i = 0; i < MaxBisectionSteps && hi - lo > 1; i++)
        {
            var mid   = lo + (hi - lo) / 2;
            var after = await CountAfterAsync(new DateTimeOffset(mid, TimeSpan.Zero)).ConfigureAwait(false);

            if (after < n)
            {
                hi = mid;
                continue;
            }

            lo = mid;

            if (after == n)
                break;
        }

        return await evaluator
                     .NextAfterAsync(definition, firstSlot, new DateTimeOffset(lo, TimeSpan.Zero), identity, ct)
                     .ConfigureAwait(false);

        async ValueTask<int> CountAfterAsync(DateTimeOffset probe)
        {
            var first = await evaluator.NextAfterAsync(definition, firstSlot, probe, identity, ct)
                                       .ConfigureAwait(false);

            if (first is not { } slot || slot > nowUtc)
                return 0;

            return chain is null
                       ? await evaluator.CountMissedAsync(definition, slot, nowUtc, n, identity, ct)
                                        .ConfigureAwait(false)
                       : await CountRememberingTheChainAsync(slot).ConfigureAwait(false);
        }

        // The bounded forward count CountMissedAsync makes over a provider grid, step for step, with the
        // answers kept so the stretch every later probe re-walks is already paid for.
        async ValueTask<int> CountRememberingTheChainAsync(DateTimeOffset from)
        {
            var walked = await BoundedOccurrenceWalker
                               .WalkAsync(from, nowUtc, (int)ProviderScheduleGrid.CountCeiling(n),
                                   includeStart: true, NextRememberedAsync)
                               .ConfigureAwait(false);

            return walked.Count;

            async ValueTask<DateTimeOffset?> NextRememberedAsync(DateTimeOffset slot)
            {
                if (chain!.TryGetValue(slot, out var remembered))
                    return remembered;

                var following = await evaluator.NextAfterAsync(definition, slot, slot, identity, ct)
                                               .ConfigureAwait(false);
                chain[slot] = following;
                return following;
            }
        }
    }
}

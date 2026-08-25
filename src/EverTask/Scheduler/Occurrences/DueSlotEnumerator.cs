namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// Turns "where the schedule's cursor is" plus "what time it is" into the list of slots that should become
/// occurrences right now. The whole misfire policy lives here; the materializer only writes what this decides.
/// </summary>
/// <remarks>
/// Every count it takes is BOUNDED. A one-second grid left behind by a three-month downtime owes eight million
/// slots, and no answer this class gives may cost eight million steps — so the caps are not a courtesy, they
/// are what keeps a catch-up decision O(1) on a uniform grid and O(cap) on a calendar one.
/// </remarks>
/// <param name="evaluator">The grid.</param>
/// <param name="misfireThreshold">
/// How old a due slot has to be, at the moment it is materialized, before the occurrence it produces says it
/// stands for MISSED work (M1). It is the same host-wide <c>SetMisfireThreshold</c> a delivery reports its own
/// lateness against, applied one step earlier: here it decides what the DECISION records on the row, there
/// what the delivery observes about itself.
/// </param>
internal sealed class DueSlotEnumerator(IScheduleEvaluator evaluator, TimeSpan misfireThreshold)
{
    /// <summary>
    /// Bound on the diagnostic counts (how many slots were dropped) for the grids that have to be WALKED. A
    /// grid that counts by division is never capped — see <see cref="CountDueAsync"/> — and whenever this
    /// bound does bite, the count travels as a lower bound and says so, so nobody reads 10,001 as a total.
    /// It is also what a count with no cap of its own falls back to, for the same reason: a number nothing
    /// bounds is a number nothing can declare exact.
    /// </summary>
    private const int DiagnosticCountCap = 10_000;

    /// <summary>
    /// Bound on the bisection that finds where the most recent N slots begin. Each step halves an interval
    /// measured in ticks, so 64 is unreachable for any representable range — it is there so a grid that
    /// answers inconsistently cannot spin.
    /// </summary>
    private const int MaxBisectionSteps = 64;

    /// <summary>
    /// Plans one materialization run.
    /// </summary>
    /// <param name="definition">The schedule.</param>
    /// <param name="cursorUtc">The slot the schedule is currently pointing at (its <c>NextRunUtc</c>).</param>
    /// <param name="nowUtc">The scheduling clock's now.</param>
    /// <param name="currentRunCount">Materializations already spent against <c>MaxRuns</c>.</param>
    /// <param name="activeOccurrences">Occurrences of this schedule that are still alive.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DueSlotPlan> PlanAsync(RecurringTask definition, DateTimeOffset cursorUtc,
                                             DateTimeOffset nowUtc, int currentRunCount, int activeOccurrences,
                                             CancellationToken ct = default)
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
            MisfirePolicy.FireOnce => await PlanFireOnceAsync(definition, settings, cursorUtc, nowUtc, capacity, ct)
                                          .ConfigureAwait(false),
            MisfirePolicy.CatchUp => await PlanCatchUpAsync(definition, settings!, cursorUtc, nowUtc, capacity,
                                              remainingRuns, ct)
                                         .ConfigureAwait(false),
            _ => await PlanSkipAsync(definition, cursorUtc, nowUtc, capacity, ct).ConfigureAwait(false)
        };

        // The run budget applies to whatever the policy chose: materializing the last allowed occurrence ends
        // the series, and it must end in the same commit that creates it, so the cursor it leaves is null.
        // The flag says WHY it is null, because the caller may end up writing that occurrence at a slot
        // further along the grid — a slot the plan named may turn out to have a row already — and a budget
        // that is spent stays spent wherever the write lands.
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
    /// What a run needs when it walks past a slot that already has an occurrence: the policy was decided for
    /// this episode when the run planned it, nothing a walk does can change that decision, and the only
    /// question left is where the grid goes next. One step, against the full plan a re-decision would cost.
    /// </remarks>
    public async ValueTask<DateTimeOffset?> NextSlotAfterAsync(RecurringTask definition, DateTimeOffset slot,
                                                               CancellationToken ct = default) =>
        await evaluator.NextAfterAsync(definition, slot, slot, ct).ConfigureAwait(false);

    /// <summary>
    /// The legacy policy, in durable clothing: run the slot only while it is still the current one, otherwise
    /// move the cursor past everything that went by (M12).
    /// </summary>
    /// <remarks>
    /// "Still current" is decided on the NATURAL successor, bounds ignored — the same question the recovery
    /// grace window asks, and for the same reason: the bounded successor is null both while the slot is
    /// current and once the series has ended, and reading that null as the former runs a months-old slot.
    /// </remarks>
    private async Task<DueSlotPlan> PlanSkipAsync(RecurringTask definition, DateTimeOffset cursorUtc,
                                                  DateTimeOffset nowUtc, int capacity, CancellationToken ct)
    {
        var successor = await evaluator.NextGridOccurrenceAfterAsync(definition, cursorUtc, ct).ConfigureAwait(false);

        if (successor is { } next && next > nowUtc)
        {
            // Still the current slot. With no capacity left the cursor stays put: the slot has not gone stale,
            // it is only waiting for the previous occurrence to end.
            if (capacity == 0)
                return new DueSlotPlan { NextCursorUtc = cursorUtc, StopReason = DueSlotStopReason.WindowFull };

            return new DueSlotPlan
            {
                Slots         = [cursorUtc],
                NextCursorUtc = await evaluator.NextAfterAsync(definition, cursorUtc, cursorUtc, ct)
                                               .ConfigureAwait(false)
            };
        }

        var skipped = await CountDueAsync(definition, cursorUtc, nowUtc, DiagnosticCountCap, ct).ConfigureAwait(false);

        return new DueSlotPlan
        {
            NextCursorUtc = await evaluator.NextAfterAsync(definition, cursorUtc, nowUtc, ct).ConfigureAwait(false),
            Losses        = Loss(SlotLossReason.SkippedByPolicy, cursorUtc, skipped)
        };
    }

    /// <summary>
    /// Collapses the whole run of due slots into ONE occurrence, at the most recent of them (M12). The age
    /// window gates that one slot: when even the newest missed slot is too old, nothing fires at all.
    /// </summary>
    private async Task<DueSlotPlan> PlanFireOnceAsync(RecurringTask definition, MisfireSettings? settings,
                                                      DateTimeOffset cursorUtc, DateTimeOffset nowUtc, int capacity,
                                                      CancellationToken ct)
    {
        // Nothing may be created and no age window can drop anything either: there is no decision left to
        // take, so the backlog is neither counted nor probed. Every operational retry of a schedule whose
        // window is full comes through here.
        if (capacity == 0 && settings?.MaxAge is null)
            return new DueSlotPlan { NextCursorUtc = cursorUtc, StopReason = DueSlotStopReason.WindowFull };

        var episode = await CountDueAsync(definition, cursorUtc, nowUtc, DiagnosticCountCap, ct)
                          .ConfigureAwait(false);

        // One due slot is the ordinary case and costs no probing: the cursor IS the newest due slot.
        var lastDue = episode.Count <= 1
                          ? cursorUtc
                          : await FindNthSlotFromEndAsync(definition, cursorUtc, nowUtc, 1, ct).ConfigureAwait(false)
                            ?? cursorUtc;

        if (settings?.MaxAge is { } age && lastDue < AgeCutoff(nowUtc, age))
        {
            return new DueSlotPlan
            {
                NextCursorUtc = await evaluator.NextAfterAsync(definition, cursorUtc, nowUtc, ct)
                                               .ConfigureAwait(false),
                Losses        = Loss(SlotLossReason.MisfireWindowExceeded, cursorUtc, episode)
            };
        }

        if (capacity == 0)
            return new DueSlotPlan { NextCursorUtc = cursorUtc, StopReason = DueSlotStopReason.WindowFull };

        return new DueSlotPlan
        {
            Slots         = [lastDue],
            NextCursorUtc = await evaluator.NextAfterAsync(definition, lastDue, lastDue, ct).ConfigureAwait(false),
            Misfire = IsMissed(cursorUtc, nowUtc, episode.Count)
                          ? new OccurrenceMisfire(MisfireKind.FireOnce, cursorUtc, lastDue, episode.Count,
                              episode.IsExact)
                          : null
        };
    }

    /// <summary>
    /// Replays the backlog one occurrence per slot, inside the three limits of <see cref="CatchUpOptions"/>:
    /// the age window drops what is too old, the per-episode cap decides whether the episode runs at all, and
    /// the concurrency budget decides how much of it runs now (M10).
    /// </summary>
    private async Task<DueSlotPlan> PlanCatchUpAsync(RecurringTask definition, MisfireSettings settings,
                                                     DateTimeOffset cursorUtc, DateTimeOffset nowUtc, int capacity,
                                                     int remainingRuns, CancellationToken ct)
    {
        var maxOccurrences = settings.MaxOccurrences ?? 1;
        var ageCutoff      = AgeCutoff(nowUtc, settings.MaxAge ?? TimeSpan.Zero);

        var losses = new List<SlotLoss>(2);

        var firstEligible = cursorUtc;

        if (cursorUtc < ageCutoff)
        {
            var windowStart = await FirstSlotOnOrAfterAsync(definition, cursorUtc, ageCutoff, ct)
                                  .ConfigureAwait(false);

            // Nothing inside the window is due (or the grid ended): drop the whole backlog and point the
            // cursor at whatever comes next — the first future slot, or nothing at all.
            if (windowStart is not { } eligibleSlot || eligibleSlot > nowUtc)
            {
                var dropped = await CountDueAsync(definition, cursorUtc, nowUtc, DiagnosticCountCap, ct)
                                  .ConfigureAwait(false);

                return new DueSlotPlan
                {
                    NextCursorUtc = windowStart,
                    Losses        = Loss(SlotLossReason.MisfireWindowExceeded, cursorUtc, dropped)
                };
            }

            var aged = await CountDueAsync(definition, cursorUtc, eligibleSlot.AddTicks(-1), DiagnosticCountCap, ct)
                           .ConfigureAwait(false);

            if (aged.Count > 0)
                losses.Add(new SlotLoss(SlotLossReason.MisfireWindowExceeded, cursorUtc, aged.Count, aged.IsExact));

            firstEligible = eligibleSlot;
        }

        // Bounded by construction: the count stops one past the cap, which is exactly what tells "at most the
        // cap" from "more than the cap" without ever enumerating the backlog.
        var eligible = await CountDueAsync(definition, firstEligible, nowUtc, maxOccurrences, ct)
                           .ConfigureAwait(false);

        if (eligible.Count > maxOccurrences)
        {
            if (settings.OverflowPolicy == CatchUpOverflowPolicy.Halt)
            {
                // The breaker is about the SIZE of a replay, so it only fires where a replay of that size can
                // happen. A series whose run budget cannot even reach the cap will create at most
                // `remainingRuns` occurrences and then close, whatever the backlog holds — halting it instead
                // writes a marker that by contract never releases itself, so an operator would have to resume
                // a schedule for the sole purpose of spending its last run. The cap and the budget are two
                // bounds on the same thing, and the smaller one is the one that decides.
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
                // axis, each probe a bounded forward count, so a grid that owes millions of slots costs a few
                // dozen probes instead of an enumeration. Deterministic grids only — every built-in one is.
                // It runs whatever the run budget is: "the most recent matter" is the policy the caller chose,
                // and a series down to its last run still owes them the newest slot rather than the oldest.
                var start = await FindNthSlotFromEndAsync(definition, firstEligible, nowUtc, maxOccurrences, ct)
                                .ConfigureAwait(false);

                if (start is { } keepFrom && keepFrom > firstEligible)
                {
                    var dropped = await CountDueAsync(definition, firstEligible, keepFrom.AddTicks(-1),
                                          DiagnosticCountCap, ct)
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
            : await evaluator.EnumerateDueSlotsAsync(definition, firstEligible, nowUtc, take, ct)
                             .ConfigureAwait(false);

        var nextCursor = slots.Count > 0
                             ? await evaluator.NextAfterAsync(definition, slots[^1], slots[^1], ct)
                                              .ConfigureAwait(false)
                             : firstEligible;

        // The misfire is stamped on the rows this plan grants, so it is built only when there ARE rows and
        // only when there is a backlog to describe: a schedule keeping up owes one slot inside the threshold
        // and reports no misfire, and a plan whose window is full grants nothing at all — paying a bisection
        // over the whole remaining backlog, at every operational retry, to close a range nobody would read.
        OccurrenceMisfire? misfire = null;

        if (slots.Count > 0 && IsMissed(firstEligible, nowUtc, eligible.Count))
        {
            // One slot is its own range, and asking for the newest of one costs a bisection to be told what
            // the caller already holds.
            var lastEligible = eligible.Count > 1
                                   ? await LastEligibleSlotAsync(definition, firstEligible, nowUtc, ct)
                                         .ConfigureAwait(false)
                                   : firstEligible;

            misfire = new OccurrenceMisfire(MisfireKind.CatchUp, firstEligible, lastEligible, eligible.Count,
                eligible.IsExact);
        }

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
    /// keeping up (M1).
    /// </summary>
    /// <remarks>
    /// Two ways to be missed, and both have to be here. A slot that came due more than
    /// <c>MisfireThreshold</c> ago is missed however alone it is — a single hourly slot replayed ten minutes
    /// after an outage is exactly what a replaying policy exists for, and reporting it as an ordinary
    /// occurrence left the handler to work that out from the delivery's own lateness. And a run of MORE than
    /// one slot is missed however young it is: the policy is about to collapse or replay slots that nothing
    /// ran, and P5 does not allow that to happen unreported just because a per-second grid fell two seconds
    /// behind. So the threshold widens the definition and never narrows it (decisions §3.5).
    /// </remarks>
    /// <remarks>
    /// Not private: the materializer asks the same question again when a run walks past slots that already
    /// had a row, because the backlog it restates on the row it does create is a smaller one. Two copies of
    /// this rule would drift the day the threshold moved.
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
    /// An age window wider than the calendar is a legal value — <see cref="CatchUpOptions"/> only refuses a
    /// non-positive one — and it means "never drop a slot for being old". Computed literally it throws, and it
    /// throws again at every operational retry of that schedule, so the series would never materialize
    /// anything again.
    /// </remarks>
    private static DateTimeOffset AgeCutoff(DateTimeOffset nowUtc, TimeSpan age) =>
        age >= nowUtc - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : nowUtc - age;

    /// <summary>
    /// The newest slot of the backlog this plan describes — the one that closes the range its
    /// <see cref="OccurrenceMisfire.MissedCount"/> counts.
    /// </summary>
    /// <remarks>
    /// It is NOT the last slot the plan materializes: the concurrency budget usually truncates that to one,
    /// and a range of one slot carrying a count of five would be two halves of the same record contradicting
    /// each other. Found by the same instant-axis bisection the rest of this class uses. The caller must
    /// already know there is more than one due slot.
    /// </remarks>
    private async ValueTask<DateTimeOffset> LastEligibleSlotAsync(RecurringTask definition,
                                                                  DateTimeOffset firstEligible,
                                                                  DateTimeOffset nowUtc, CancellationToken ct) =>
        await FindNthSlotFromEndAsync(definition, firstEligible, nowUtc, 1, ct).ConfigureAwait(false)
        ?? firstEligible;

    /// <summary>
    /// The first grid slot at or after <paramref name="instant"/>, walking forward from a known real
    /// occurrence. The grid only answers "strictly after", so the probe sits one tick earlier.
    /// </summary>
    private async ValueTask<DateTimeOffset?> FirstSlotOnOrAfterAsync(RecurringTask definition, DateTimeOffset anchor,
                                                                     DateTimeOffset instant, CancellationToken ct) =>
        instant <= anchor
            ? anchor
            : await evaluator.NextAfterAsync(definition, anchor, instant.AddTicks(-1), ct).ConfigureAwait(false);

    /// <summary>
    /// How many slots are due in <c>[fromSlot, through]</c>, and whether that is the real total. Zero when the
    /// window is empty.
    /// </summary>
    /// <remarks>
    /// The cap is lifted for a grid that counts by division: it costs one subtraction there, and capping it
    /// would turn "a three-month outage lost 7,900,000 runs" into "it lost 10,001" with nothing to say which
    /// of the two the number is. Where the cap does apply the walk spends it in full — a cap of twenty
    /// thousand is answered by twenty thousand steps, not by the walk's own smaller bound — the count stops
    /// one past it, and <see cref="SlotCount.IsExact"/> is what keeps that from being reported as a total.
    /// </remarks>
    private async ValueTask<SlotCount> CountDueAsync(RecurringTask definition, DateTimeOffset fromSlot,
                                                     DateTimeOffset through, int cap, CancellationToken ct)
    {
        if (through < fromSlot)
            return new SlotCount(0, true);

        var countsExactly = definition.CountsMissedInConstantTime();

        // A walked grid is never asked for an UNCAPPED count: int.MaxValue means "no cap", the walk keeps a
        // bound of its own for that ask, and an answer that stops there is a lower bound wearing the shape of
        // a total. MaxOccurrences is an int and may well be int.MaxValue — a cap no backlog can exceed, so
        // the decision is unaffected either way, but the number it reports has to stay honest.
        var walkCap = cap == int.MaxValue ? DiagnosticCountCap : cap;

        var count = await evaluator
                          .CountMissedAsync(definition, fromSlot, through, countsExactly ? int.MaxValue : walkCap, ct)
                          .ConfigureAwait(false);

        return new SlotCount(count, countsExactly || count <= walkCap);
    }

    /// <summary>A diagnostic count together with whether it is the real total or only a lower bound.</summary>
    private readonly record struct SlotCount(int Count, bool IsExact);

    /// <summary>
    /// Where the most recent <paramref name="n"/> due slots begin, found by bisecting the instant axis between
    /// <paramref name="firstSlot"/> and <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// The count of slots strictly after an instant only ever decreases as that instant moves forward, and it
    /// steps down by exactly one at each slot — so the boundary where it equals <paramref name="n"/> is the
    /// slot we want, and a plain binary search finds it. Each probe is a forward count bounded at
    /// <paramref name="n"/>, which is what makes the whole thing affordable on a grid whose backlog nobody can
    /// afford to enumerate. The caller must already know there are MORE than <paramref name="n"/> due slots.
    /// </remarks>
    private async Task<DateTimeOffset?> FindNthSlotFromEndAsync(RecurringTask definition, DateTimeOffset firstSlot,
                                                                DateTimeOffset nowUtc, int n, CancellationToken ct)
    {
        var lo = firstSlot.UtcTicks;
        var hi = nowUtc.UtcTicks;

        for (var i = 0; i < MaxBisectionSteps && hi - lo > 1; i++)
        {
            var mid = lo + (hi - lo) / 2;

            if (await CountAfterAsync(new DateTimeOffset(mid, TimeSpan.Zero)).ConfigureAwait(false) >= n)
                lo = mid;
            else
                hi = mid;
        }

        return await evaluator.NextAfterAsync(definition, firstSlot, new DateTimeOffset(lo, TimeSpan.Zero), ct)
                              .ConfigureAwait(false);

        async ValueTask<int> CountAfterAsync(DateTimeOffset probe)
        {
            var first = await evaluator.NextAfterAsync(definition, firstSlot, probe, ct).ConfigureAwait(false);

            return first is not { } slot || slot > nowUtc
                       ? 0
                       : await evaluator.CountMissedAsync(definition, slot, nowUtc, n, ct).ConfigureAwait(false);
        }
    }
}

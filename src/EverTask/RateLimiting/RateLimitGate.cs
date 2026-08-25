using System.Collections.Concurrent;
using EverTask.Configuration;

namespace EverTask.RateLimiting;

/// <summary>
/// Default <see cref="IRateLimitGate"/> implementation (see interface remarks for the contract).
/// </summary>
internal sealed class RateLimitGate(
    IKeyedRateLimiter limiter,
    IScheduler scheduler,
    IGateInvalidationRegistry invalidationRegistry,
    RateLimitParkingLot parkingLot,
    EverTaskServiceConfiguration configuration,
    IEverTaskLogger<RateLimitGate> logger,
    TimeProvider? timeProvider = null) : IRateLimitGate
{
    // The gate hands slots to the scheduler, so it must read the SAME clock the scheduler sleeps on (P9).
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private long _lastSeenFailOpenCount;
    private long _lastFailOpenEventTicks;
    private long _lastAggregationSweepTicks;
    private sealed class DeferralAggregation
    {
        public long WindowStartTicks;
        public int  DeferralsSinceLastEvent;
    }

    private readonly ConcurrentDictionary<(Type TaskType, string Key), DeferralAggregation> _deferralAggregations = new();
    private static readonly ConcurrentDictionary<Type, byte> EmptyKeyWarned = new();

    /// <summary>
    /// Floor applied to a re-park slot that is not in the future (anti busy-spin). Never applied
    /// to future slots: a flat clamp would systematically overshoot the GCRA slot.
    /// Internal for testing purposes.
    /// </summary>
    internal TimeSpan PastSlotFloor { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Aggregation window for deferral events: one event at the first deferral of a
    /// (task type, key) window, then one summary per window while throttling persists.
    /// Internal for testing purposes.
    /// </summary>
    internal TimeSpan DeferralEventWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Aggregation entries with no deferral for this long are swept (churning per-customer keys
    /// must not accumulate forever). Internal for testing purposes.
    /// </summary>
    internal TimeSpan AggregationEntryTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Minimum interval between opportunistic aggregation sweeps. Internal for testing purposes.</summary>
    internal TimeSpan AggregationSweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Number of live deferral-aggregation entries. Internal for testing purposes.</summary>
    internal int DeferralAggregationCount => _deferralAggregations.Count;

    public async ValueTask<RateLimitGateResult> TryPassAsync(TaskHandlerExecutor task, CancellationToken ct)
    {
        var policy = task.RateLimitPolicy;
        if (policy == null)
            return new RateLimitGateResult(RateLimitGateOutcome.Proceed);

        var taskType = task.Task.GetType();
        var key      = task.RateLimitKey;

        if (string.IsNullOrEmpty(key))
        {
            // A policy without a key is a configuration mistake: warn once per task type and
            // execute ungated (fail-safe)
            if (EmptyKeyWarned.TryAdd(taskType, 0))
                logger.EmptyRateLimitKey(taskType.Name);

            return new RateLimitGateResult(RateLimitGateOutcome.Proceed);
        }

        // Captured BEFORE any decision: a Cancel / same-taskKey re-dispatch arriving from here on
        // bumps the epoch, and the set-then-check after Schedule will observe it
        var epoch = invalidationRegistry.GetEpoch(task.PersistenceId);

        var decision = await AcquireFailOpenAsync(policy, taskType, key, task.PersistenceId, ct).ConfigureAwait(false);

        // A previously-parked task delivered back to a worker is no longer parked (idempotent
        // no-op otherwise); a re-deferral below re-registers it
        parkingLot.Remove(task.PersistenceId);

        if (decision.Acquired)
            return Proceed();

        // L8 Discard: no waiting, no parking — the task is terminally rejected when no budget
        // is immediately available. The freed reservation is released best-effort.
        if (policy.OverflowBehavior == RateLimitOverflowBehavior.Discard)
        {
            _ = ReleaseBestEffortAsync(taskType, key, task.PersistenceId);
            return Reject(RateLimitRejectionKind.Discarded, decision.RetryAt);
        }

        // L14: never wait inline on the consumer. A near slot is re-parked to the scheduler (Defer)
        // exactly like a far slot, so the consumer is immediately free for the next item — on a
        // single-consumer queue an inline Task.Delay here head-of-line-blocked every following item,
        // INCLUDING tasks without any policy. The task still fires at its reserved slot via redelivery
        // (the reservation is redeemed then, see InMemoryKeyedRateLimiter; honored even under congested
        // redelivery latency, L22). The previous in-slot wait only saved a scheduler round-trip for
        // near slots — a latency optimisation not worth blocking the consumer. (RateLimitPolicy.
        // MaxInSlotWait is retained for binary compatibility but no longer drives an inline wait.)
        return Defer(task, taskType, key, decision.RetryAt, epoch);
    }

    /// <inheritdoc />
    public ValueTask WaitForParkingCapacityAsync(TaskHandlerExecutor task, CancellationToken ct)
    {
        // Fast path: the lot is under cap virtually always
        if (parkingLot.Count < parkingLot.MaxParkedTasks)
            return ValueTask.CompletedTask;

        return parkingLot.WaitForCapacityAsync(EffectiveQueueName(task), ct);
    }

    /// <summary>
    /// Delay applied by <see cref="ReparkInFlightRedelivery"/>. Internal for testing purposes.
    /// </summary>
    internal TimeSpan InFlightRedeliveryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public void ReparkInFlightRedelivery(TaskHandlerExecutor task)
    {
        // Latest-wins guard: if a registration already exists for this task (e.g. a newer
        // same-taskKey payload was already re-parked), this stale redelivery must NOT
        // overwrite it — Schedule is latest-wins. The parked copy delivers on its own.
        if (scheduler.IsScheduled(task.PersistenceId))
            return;

        // Same set-then-check discipline as Defer: capture the epoch BEFORE Schedule so a
        // Cancel / re-dispatch racing this re-park is observed afterwards
        var epoch = invalidationRegistry.GetEpoch(task.PersistenceId);
        var slot  = _timeProvider.GetUtcNow() + InFlightRedeliveryDelay;

        var parked = task.ToLazy();

        bool reparked;

        if (task.RecurringTask != null)
        {
            // F14: same RunUntil guard as the Defer path — an occurrence whose re-park slot falls past
            // RunUntil must never be fired late. Drop this redelivery; the in-flight original advances
            // the series (its next-occurrence calculation lands past RunUntil and ends the series).
            var runUntil = task.RecurringTask.RunUntil;
            // H: RunUntil is EXCLUSIVE for the recurrence (CalculateNextRun drops an occurrence whose time
            // is >= RunUntil), so the gate drops a reserved slot AT or past RunUntil for consistency — an
            // occurrence the recurrence would consider ended must not be fired by the limiter at the boundary tick.
            if (runUntil.HasValue && slot >= runUntil.Value)
            {
                logger.InFlightRedeliveryDroppedPastRunUntil(
                    task.PersistenceId, task.RateLimitKey, slot, runUntil.Value);
                return;
            }

            // Same occurrence, ExecutionTime untouched (schedule-drift rule)
            reparked = scheduler.TrySchedule(parked, nextRecurringRun: slot);
        }
        else
        {
            parked   = ReparkOneShot(parked, slot);
            reparked = scheduler.TrySchedule(parked);
        }

        // The IsScheduled guard above cannot see a registration made after it, and the conditional
        // registration is what covers that gap: a reschedule that parked its own executor in between owns
        // the series, and this redelivery has nothing left to re-park.
        if (!reparked)
        {
            logger.DeferralRefusedBySuccessor(task.PersistenceId);
            return;
        }

        // L2 accounting: the redelivery's enqueue already removed the original lot entry, so
        // the re-park must re-register it (idempotent) or the bound under-counts
        if (!string.IsNullOrEmpty(task.RateLimitKey))
            parkingLot.Park(task.PersistenceId, EffectiveQueueName(task), task.RateLimitKey, slot);

        DropStaleRegistrationIfInvalidated(task, task.Task.GetType(), task.RateLimitKey, parked, epoch);

        logger.InFlightRedeliveryReparked(task.PersistenceId, slot);
    }

    /// <summary>
    /// Set-then-check shared by the Defer path and <see cref="ReparkInFlightRedelivery"/>: if
    /// the invalidation epoch moved while the task sat in the dequeue→re-park limbo, drop OUR
    /// registration and bookkeeping. The conditional unschedule covers "our registration is
    /// still parked"; it can also fail because the invalidator's own unconditional
    /// TryUnschedule already removed it (Cancel or same-taskKey re-dispatch landing between our
    /// Schedule and here) — then NOTHING is registered anymore and the parking-lot entry would
    /// leak forever (the task never re-enqueues, so nothing decrements it).
    /// <see cref="IScheduler.IsScheduled"/> distinguishes that case from "a newer registration
    /// took over", whose parking entry and reservation must survive untouched.
    /// </summary>
    private void DropStaleRegistrationIfInvalidated(TaskHandlerExecutor task, Type taskType, string? key,
                                                    TaskHandlerExecutor parked, long epoch)
    {
        if (!invalidationRegistry.HasChangedSince(task.PersistenceId, epoch))
            return;

        if (scheduler.TryUnschedule(task.PersistenceId, parked) || !scheduler.IsScheduled(task.PersistenceId))
        {
            logger.StaleParkedRegistrationDropped(task.PersistenceId);
            parkingLot.Remove(task.PersistenceId);

            if (!string.IsNullOrEmpty(key))
                _ = ReleaseBestEffortAsync(taskType, key, task.PersistenceId);
        }
    }

    /// <summary>
    /// Best-effort reservation release. The outcome never feeds back into the gate decision (an
    /// orphan reservation lapses via TTL), but the limiter's <see cref="ValueTask"/> must still be
    /// consumed exactly once — a distributed <see cref="IKeyedRateLimiter"/> may return one backed by a
    /// reusable <see cref="System.Threading.Tasks.Sources.IValueTaskSource"/>, which a discard would leave
    /// unconsumed — and a failure is observed and logged rather than lost. With the in-box limiter the
    /// release completes synchronously, so the wrapper adds no allocation. Callers discard the returned
    /// <see cref="Task"/>: it never faults.
    /// </summary>
    private async Task ReleaseBestEffortAsync(Type taskType, string key, Guid reservationId)
    {
        try
        {
            await limiter.ReleaseAsync(taskType, key, reservationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.BestEffortReleaseFailed(ex, reservationId, key);
        }
    }

    /// <summary>
    /// Moves a one-shot's <c>ExecutionTime</c> to the reserved slot — the only thing the scheduler reads for a
    /// non-recurring task — while remembering the slot the task was actually scheduled for.
    /// </summary>
    /// <remarks>
    /// Recurring occurrences keep their <c>ExecutionTime</c> (the schedule-drift rule), so only one-shots ever
    /// lose it. Without the nominal slot kept aside, a deferred task would report the limiter's slot as the
    /// time it was scheduled for, to its own handler and to the dashboard alike. An immediate task has no
    /// nominal slot to keep, and the marker makes that stay null instead of becoming the reserved one.
    /// The slot is read through <c>NominalSlotOfDelivery</c>, which honours that marker: a task re-parked a
    /// SECOND time (its first reservation evicted or expired) would otherwise pick up the first reserved
    /// slot out of <c>ExecutionTime</c> and report an immediate dispatch as having been scheduled for it.
    /// </remarks>
    private static TaskHandlerExecutor ReparkOneShot(TaskHandlerExecutor parked, DateTimeOffset slot) =>
        parked with
        {
            NominalSlotUtc = parked.NominalSlotOfDelivery,
            ExecutionTime = slot,
            ExecutionTimeIsReservedSlot = true
        };

    private static string EffectiveQueueName(TaskHandlerExecutor task) =>
        task.QueueName ?? (task.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

    /// <summary>
    /// Builds a Proceed result, surfacing the mandatory tracked-keys fail-open monitoring event
    /// when the in-memory limiter crossed the cap since the last check (rate-limited at source).
    /// </summary>
    private RateLimitGateResult Proceed()
    {
        if (limiter is InMemoryKeyedRateLimiter inMemoryLimiter)
        {
            var total = inMemoryLimiter.FailOpenCount;

            if (total > Interlocked.Read(ref _lastSeenFailOpenCount))
            {
                var nowTicks  = _timeProvider.GetUtcNow().UtcTicks;
                var lastEvent = Interlocked.Read(ref _lastFailOpenEventTicks);

                // Window CAS first, count consumed only on the emitting path: consuming the
                // delta before winning the window would swallow clustered fail-opens forever
                if (nowTicks - lastEvent >= DeferralEventWindow.Ticks
                    && Interlocked.CompareExchange(ref _lastFailOpenEventTicks, nowTicks, lastEvent) == lastEvent)
                {
                    Interlocked.Exchange(ref _lastSeenFailOpenCount, total);
                    return new RateLimitGateResult(RateLimitGateOutcome.Proceed,
                        EmitFailOpenEvent: true, TotalFailOpenCount: total);
                }
            }
        }

        return new RateLimitGateResult(RateLimitGateOutcome.Proceed);
    }

    private static RateLimitGateResult Reject(RateLimitRejectionKind kind, DateTimeOffset slot) =>
        new(RateLimitGateOutcome.Rejected, slot, RejectionKind: kind);

    /// <summary>
    /// Limiter call with the documented fail policy: a throwing limiter (e.g. a distributed
    /// implementation with the network down) fails OPEN — the task executes unthrottled.
    /// </summary>
    private async ValueTask<RateLimitDecision> AcquireFailOpenAsync(
        RateLimitPolicy policy, Type taskType, string key, Guid reservationId, CancellationToken ct)
    {
        try
        {
            return await limiter.TryAcquireAsync(policy, taskType, key, reservationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Service shutdown during an in-slot wait or limiter call: propagate, the task stays
            // in its recoverable status and is re-dispatched at the next startup
            throw;
        }
        catch (Exception ex)
        {
            logger.LimiterFailedOpen(ex, reservationId, key);
            return new RateLimitDecision(true, default);
        }
    }

    private RateLimitGateResult Defer(TaskHandlerExecutor task, Type taskType, string key,
                                      DateTimeOffset slot, long epoch)
    {
        var now = _timeProvider.GetUtcNow();

        // L3 horizon: far-future slots are never parked (the limiter did not book them either).
        // The caller applies the terminal outcome: one-shot → persisted Failed + OnError with
        // the typed exception; recurring → occurrence skipped, series alive.
        if (slot - now > task.RateLimitPolicy!.MaxReservationHorizon)
        {
            logger.SlotExceedsReservationHorizon(
                task.PersistenceId, key, slot, task.RateLimitPolicy.MaxReservationHorizon);

            return Reject(RateLimitRejectionKind.HorizonExceeded, slot);
        }

        // Anti busy-spin floor, ONLY for slots not in the future
        if (slot <= now)
            slot = now + PastSlotFloor;

        // Unconditional lazy re-park (L1): a parked task must never pin a handler instance
        var parked = task.ToLazy();

        // Whether the registration was actually made. It is refused when a NEWER schedule version is already
        // parked for this task, which is the one case where replacing it latest-wins would put a definition a
        // reschedule has already retired back in the scheduler.
        bool reparked;

        if (task.RecurringTask != null)
        {
            var runUntil = task.RecurringTask.RunUntil;
            // H: RunUntil is EXCLUSIVE for the recurrence (CalculateNextRun drops an occurrence whose time
            // is >= RunUntil), so the gate drops a reserved slot AT or past RunUntil for consistency — an
            // occurrence the recurrence would consider ended must not be fired by the limiter at the boundary tick.
            if (runUntil.HasValue && slot >= runUntil.Value)
            {
                // Never fire late: the occurrence is skipped (same semantics as downtime; the
                // caller routes it through the normal next-occurrence path so the series state
                // is updated). Free the reservation best-effort.
                logger.OccurrenceSkippedPastRunUntil(task.PersistenceId, key, slot, runUntil.Value);

                _ = ReleaseBestEffortAsync(taskType, key, task.PersistenceId);

                return Reject(RateLimitRejectionKind.OccurrencePastRunUntil, slot);
            }

            // Recurring re-park: schedule the SAME occurrence at the reserved slot without
            // touching ExecutionTime, which must keep pointing at the occurrence's scheduled
            // time (the schedule-drift fix in QueueNextOccourrence depends on it)
            reparked = scheduler.TrySchedule(parked, nextRecurringRun: slot);
        }
        else
        {
            // One-shot re-park: Schedule reads ExecutionTime
            parked   = ReparkOneShot(parked, slot);
            reparked = scheduler.TrySchedule(parked);
        }

        if (!reparked)
        {
            // A reschedule committed a new definition and parked its executor while this delivery sat in the
            // gate: the series belongs to that registration now, and this one carries the grid it replaced.
            // Nothing of ours is parked, so the lot entry below is never registered and the set-then-check
            // has nothing to unschedule; what does need undoing is the reservation this pass booked for a
            // slot no delivery will ever redeem.
            logger.DeferralRefusedBySuccessor(task.PersistenceId);
            _ = ReleaseBestEffortAsync(taskType, key, task.PersistenceId);

            return new RateLimitGateResult(RateLimitGateOutcome.Deferred, slot);
        }

        // L2 accounting: distinct parked tasks (idempotent re-registration on re-park)
        parkingLot.Park(task.PersistenceId, EffectiveQueueName(task), key, slot);

        // Set-then-check: a Cancel or same-taskKey immediate re-dispatch that happened while
        // this task sat in the dequeue→re-park limbo is invisible to TryUnschedule (nothing was
        // parked yet). If the epoch moved, drop OUR registration and bookkeeping.
        DropStaleRegistrationIfInvalidated(task, taskType, key, parked, epoch);

        logger.TaskDeferred(task.PersistenceId, key, slot, taskType);

        return DeferralResult(taskType, key, slot, now);
    }

    /// <summary>
    /// Source-side aggregation of deferral events: the first deferral of a (task type, key)
    /// window emits immediately; further deferrals accumulate and surface as one summary per
    /// window while throttling persists.
    /// </summary>
    private RateLimitGateResult DeferralResult(Type taskType, string key, DateTimeOffset slot, DateTimeOffset now)
    {
        if (!configuration.RateLimiterOptions.EmitDeferralEvents)
            return new RateLimitGateResult(RateLimitGateOutcome.Deferred, slot);

        SweepAggregationsIfDue(now.UtcTicks);

        var aggregation = _deferralAggregations.GetOrAdd((taskType, key), static _ => new DeferralAggregation());

        lock (aggregation)
        {
            aggregation.DeferralsSinceLastEvent++;

            if (now.UtcTicks - aggregation.WindowStartTicks >= DeferralEventWindow.Ticks)
            {
                var count = aggregation.DeferralsSinceLastEvent;
                aggregation.WindowStartTicks        = now.UtcTicks;
                aggregation.DeferralsSinceLastEvent = 0;
                return new RateLimitGateResult(RateLimitGateOutcome.Deferred, slot, EmitDeferralEvent: true, count);
            }
        }

        return new RateLimitGateResult(RateLimitGateOutcome.Deferred, slot);
    }

    /// <summary>
    /// Opportunistic eviction of aggregation entries whose key saw no deferral for
    /// <see cref="AggregationEntryTtl"/>: with churning per-customer keys the dictionary would
    /// otherwise grow unbounded (every other collection of the feature has a TTL or sweep).
    /// A pending summary of a long-dead window is dropped with the entry — by definition the
    /// throttling it described ended long ago.
    /// </summary>
    private void SweepAggregationsIfDue(long nowTicks)
    {
        var lastSweep = Interlocked.Read(ref _lastAggregationSweepTicks);
        if (nowTicks - lastSweep < AggregationSweepInterval.Ticks)
            return;

        // Single sweeper at a time; losers skip (the winner does the work)
        if (Interlocked.CompareExchange(ref _lastAggregationSweepTicks, nowTicks, lastSweep) != lastSweep)
            return;

        var cutoff = nowTicks - AggregationEntryTtl.Ticks;
        foreach (var kvp in _deferralAggregations)
        {
            if (Interlocked.Read(ref kvp.Value.WindowStartTicks) < cutoff)
            {
                // Reference-equality remove: a concurrent GetOrAdd re-creating the entry wins.
                // A deferral racing on the removed instance still emits correctly; the next one
                // simply starts a fresh window (at worst one extra immediate event).
                ((ICollection<KeyValuePair<(Type, string), DeferralAggregation>>)_deferralAggregations).Remove(kvp);
            }
        }
    }
}

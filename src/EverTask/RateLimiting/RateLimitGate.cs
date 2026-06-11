using System.Collections.Concurrent;

namespace EverTask.RateLimiting;

/// <summary>
/// Default <see cref="IRateLimitGate"/> implementation (see interface remarks for the contract).
/// </summary>
internal sealed class RateLimitGate(
    IKeyedRateLimiter limiter,
    IScheduler scheduler,
    IGateInvalidationRegistry invalidationRegistry,
    EverTaskServiceConfiguration configuration,
    IEverTaskLogger<RateLimitGate> logger) : IRateLimitGate
{
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
            {
                logger.LogWarning(
                    "Task type {TaskType} declares a RateLimitPolicy but produced a null/empty rate-limit key: " +
                    "tasks execute WITHOUT rate limiting. Implement IRateLimitedTask or override GetRateLimitKey",
                    taskType.Name);
            }

            return new RateLimitGateResult(RateLimitGateOutcome.Proceed);
        }

        // Captured BEFORE any decision: a Cancel / same-taskKey re-dispatch arriving from here on
        // bumps the epoch, and the set-then-check after Schedule will observe it
        var epoch = invalidationRegistry.GetEpoch(task.PersistenceId);

        var decision = await AcquireFailOpenAsync(policy, taskType, key, task.PersistenceId, ct).ConfigureAwait(false);
        if (decision.Acquired)
            return new RateLimitGateResult(RateLimitGateOutcome.Proceed);

        // In-slot wait: a near slot is awaited inline and redeemed — closes the guard-overlap
        // edge (no redelivery racing the in-flight guard) and saves a scheduler round-trip.
        // Bounded: redemption at/after the slot returns Acquired by the limiter contract.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var wait = decision.RetryAt - DateTimeOffset.UtcNow;
            if (wait > policy.MaxInSlotWait)
                break;

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            decision = await AcquireFailOpenAsync(policy, taskType, key, task.PersistenceId, ct).ConfigureAwait(false);
            if (decision.Acquired)
                return new RateLimitGateResult(RateLimitGateOutcome.Proceed);
        }

        return Defer(task, taskType, key, decision.RetryAt, epoch);
    }

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
            logger.LogWarning(ex,
                "Rate limiter failed for task {TaskId} (key {Key}): failing OPEN, the task executes unthrottled",
                reservationId, key);
            return new RateLimitDecision(true, default);
        }
    }

    private RateLimitGateResult Defer(TaskHandlerExecutor task, Type taskType, string key,
                                      DateTimeOffset slot, long epoch)
    {
        var now = DateTimeOffset.UtcNow;

        // Anti busy-spin floor, ONLY for slots not in the future
        if (slot <= now)
            slot = now + PastSlotFloor;

        // Unconditional lazy re-park (L1): a parked task must never pin a handler instance
        var parked = task.ToLazy();

        if (task.RecurringTask != null)
        {
            var runUntil = task.RecurringTask.RunUntil;
            if (runUntil.HasValue && slot > runUntil.Value)
            {
                // Never fire late: the occurrence is dropped (skipped occurrences follow the
                // same semantics as downtime). Free the reservation best-effort.
                logger.LogWarning(
                    "Rate limit: occurrence of recurring task {TaskId} (key {Key}) dropped — reserved slot " +
                    "{SlotUtc:O} falls past RunUntil {RunUntil:O}",
                    task.PersistenceId, key, slot, runUntil.Value);

                _ = limiter.ReleaseAsync(taskType, key, task.PersistenceId, CancellationToken.None);

                return DeferralResult(taskType, key, slot, now);
            }

            // Recurring re-park: schedule the SAME occurrence at the reserved slot without
            // touching ExecutionTime, which must keep pointing at the occurrence's scheduled
            // time (the schedule-drift fix in QueueNextOccourrence depends on it)
            scheduler.Schedule(parked, nextRecurringRun: slot);
        }
        else
        {
            // One-shot re-park: Schedule reads ExecutionTime
            parked = parked with { ExecutionTime = slot };
            scheduler.Schedule(parked);
        }

        // Set-then-check: a Cancel or same-taskKey immediate re-dispatch that happened while
        // this task sat in the dequeue→re-park limbo is invisible to TryUnschedule (nothing was
        // parked yet). If the epoch moved, drop OUR registration — conditionally, so a newer
        // registration for the same task survives — and free the reservation.
        if (invalidationRegistry.HasChangedSince(task.PersistenceId, epoch))
        {
            if (scheduler.TryUnschedule(task.PersistenceId, parked))
            {
                logger.LogDebug(
                    "Rate limit: parked registration of task {TaskId} dropped (cancelled or re-dispatched during re-park)",
                    task.PersistenceId);
                _ = limiter.ReleaseAsync(taskType, key, task.PersistenceId, CancellationToken.None);
            }
        }

        logger.LogDebug(
            "Rate limit deferred task {TaskId}: key={Key} slotUtc={SlotUtc:O} policy={TaskType}",
            task.PersistenceId, key, slot, taskType);

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
}

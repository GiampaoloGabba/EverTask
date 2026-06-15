using EverTask.Handler;
using EverTask.Logger;
using EverTask.RateLimiting;
using EverTask.Resilience;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverTask.Tests.RateLimiting;

/// <summary>
/// Unit tests for <see cref="RateLimitGate"/> mechanics: bypasses, re-park branches (one-shot vs
/// recurring), in-slot wait, past-slot floor, fail-open, invalidation set-then-check, deferral
/// event aggregation — and the WorkerExecutor-level invariants (no storage write on deferral,
/// blacklist before the gate).
/// </summary>
public class RateLimitGateTests
{
    // Dedicated task records: the gate keeps once-per-type warning state, so each test that
    // needs a pristine type uses its own
    private sealed record GateTaskA : IEverTask;
    public sealed record GateTaskB : IEverTask; // public: referenced by the public blocking handler below
    private sealed record GateTaskEmptyKey : IEverTask;

    private readonly Mock<IKeyedRateLimiter> _limiter   = new();
    private readonly Mock<IScheduler> _scheduler        = new();
    private readonly GateInvalidationRegistry _registry = new();
    private readonly EverTaskServiceConfiguration _configuration = new();
    private readonly RateLimitParkingLot _parkingLot;

    public RateLimitGateTests()
    {
        var options = new EverTask.RateLimiting.RateLimiterOptions();
        options.ResolveDefaults(1000);
        _parkingLot = new RateLimitParkingLot(options);
    }

    private RateLimitGate CreateGate(IKeyedRateLimiter? realLimiter = null) =>
        new(realLimiter ?? _limiter.Object, _scheduler.Object, _registry, _parkingLot, _configuration,
            new Mock<IEverTaskLogger<RateLimitGate>>().Object);

    private static RateLimitPolicy Policy(TimeSpan? maxInSlotWait = null) =>
        new(1, TimeSpan.FromSeconds(10))
        {
            Burst         = 1,
            MaxInSlotWait = maxInSlotWait ?? TimeSpan.FromSeconds(1)
        };

    private static TaskHandlerExecutor CreateExecutor(
        RateLimitPolicy? policy,
        string? key,
        IEverTask? task = null,
        RecurringTask? recurring = null,
        DateTimeOffset? executionTime = null) =>
        new(task ?? new GateTaskA(),
            new object(),
            null,
            executionTime,
            recurring,
            null,
            null,
            null,
            null,
            Guid.NewGuid(),
            "default",
            null,
            AuditLevel.Full,
            policy,
            key);

    private void SetupDeferral(DateTimeOffset slot) =>
        _limiter.Setup(l => l.TryAcquireAsync(It.IsAny<RateLimitPolicy>(), It.IsAny<Type>(), It.IsAny<string>(),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RateLimitDecision(false, slot));

    private void SetupAcquired() =>
        _limiter.Setup(l => l.TryAcquireAsync(It.IsAny<RateLimitPolicy>(), It.IsAny<Type>(), It.IsAny<string>(),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RateLimitDecision(true, default));

    // ---------------------------------------------------------------- bypasses

    [Fact]
    public async Task Should_proceed_without_touching_limiter_when_no_policy()
    {
        var gate   = CreateGate();
        var result = await gate.TryPassAsync(CreateExecutor(policy: null, key: "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Proceed);
        _limiter.VerifyNoOtherCalls();
        _scheduler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_proceed_without_throttling_when_key_is_empty()
    {
        var gate = CreateGate();

        var first  = await gate.TryPassAsync(CreateExecutor(Policy(), key: null, task: new GateTaskEmptyKey()), CancellationToken.None);
        var second = await gate.TryPassAsync(CreateExecutor(Policy(), key: "", task: new GateTaskEmptyKey()), CancellationToken.None);

        first.Outcome.ShouldBe(RateLimitGateOutcome.Proceed);
        second.Outcome.ShouldBe(RateLimitGateOutcome.Proceed);
        _limiter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_proceed_when_budget_acquired()
    {
        SetupAcquired();
        var gate   = CreateGate();
        var result = await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Proceed);
        _scheduler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_fail_open_when_limiter_throws()
    {
        _limiter.Setup(l => l.TryAcquireAsync(It.IsAny<RateLimitPolicy>(), It.IsAny<Type>(), It.IsAny<string>(),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("network down"));

        var gate   = CreateGate();
        var result = await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Proceed,
            "a failing limiter must fail OPEN (never-lose-a-task contract)");
        _scheduler.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- re-park branches

    [Fact]
    public async Task Should_repark_one_shot_lazily_at_reserved_slot_when_deferred()
    {
        var slot = DateTimeOffset.UtcNow.AddSeconds(8);
        SetupDeferral(slot);

        TaskHandlerExecutor? parked = null;
        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), null))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((e, _) => parked = e);

        var gate     = CreateGate();
        var executor = CreateExecutor(Policy(), "k");
        var result   = await gate.TryPassAsync(executor, CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred);
        result.SlotUtc.ShouldBe(slot);

        parked.ShouldNotBeNull();
        ReferenceEquals(parked, executor).ShouldBeFalse("re-park must use a NEW registration instance");
        parked.IsLazy.ShouldBeTrue("re-park is unconditionally lazy (no pinned handler instance)");
        parked.ExecutionTime.ShouldBe(slot, "one-shot re-park stamps the reserved slot on ExecutionTime");
        parked.PersistenceId.ShouldBe(executor.PersistenceId);
        parked.RateLimitPolicy.ShouldBe(executor.RateLimitPolicy, "rate-limit fields survive the re-park");
        parked.RateLimitKey.ShouldBe("k");
    }

    [Fact]
    public async Task Should_repark_recurring_at_slot_without_touching_execution_time()
    {
        var slot = DateTimeOffset.UtcNow.AddSeconds(8);
        SetupDeferral(slot);

        var occurrenceTime = DateTimeOffset.UtcNow.AddSeconds(-1);
        var recurring      = new RecurringTask { SecondInterval = new SecondInterval(30) };

        TaskHandlerExecutor? parked = null;
        DateTimeOffset? nextRecurringRun = null;
        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((e, n) =>
                  {
                      parked           = e;
                      nextRecurringRun = n;
                  });

        var gate     = CreateGate();
        var executor = CreateExecutor(Policy(), "k", recurring: recurring, executionTime: occurrenceTime);
        var result   = await gate.TryPassAsync(executor, CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred);

        parked.ShouldNotBeNull();
        parked.IsLazy.ShouldBeTrue();
        nextRecurringRun.ShouldBe(slot, "recurring re-park schedules via nextRecurringRun");
        parked.ExecutionTime.ShouldBe(occurrenceTime,
            "ExecutionTime must keep pointing at the occurrence's scheduled time (schedule-drift fix)");
    }

    [Fact]
    public async Task Should_drop_recurring_occurrence_when_slot_falls_past_run_until()
    {
        var slot = DateTimeOffset.UtcNow.AddSeconds(30);
        SetupDeferral(slot);

        var recurring = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            RunUntil       = DateTimeOffset.UtcNow.AddSeconds(5)
        };

        var gate     = CreateGate();
        var executor = CreateExecutor(Policy(), "k", recurring: recurring, executionTime: DateTimeOffset.UtcNow);
        var result   = await gate.TryPassAsync(executor, CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Rejected, "the occurrence must not execute");
        result.RejectionKind.ShouldBe(RateLimitRejectionKind.OccurrencePastRunUntil);
        _scheduler.Verify(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()), Times.Never,
            "an occurrence past RunUntil is skipped, never fired late");
        _limiter.Verify(l => l.ReleaseAsync(It.IsAny<Type>(), "k", executor.PersistenceId, It.IsAny<CancellationToken>()),
            Times.Once, "the unredeemable reservation is released best-effort");
    }

    [Fact]
    public async Task Should_reject_when_slot_exceeds_reservation_horizon()
    {
        var policy = new RateLimitPolicy(1, TimeSpan.FromSeconds(10))
        {
            Burst                 = 1,
            MaxReservationHorizon = TimeSpan.FromSeconds(5)
        };
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(30));

        var gate   = CreateGate();
        var result = await gate.TryPassAsync(CreateExecutor(policy, "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Rejected);
        result.RejectionKind.ShouldBe(RateLimitRejectionKind.HorizonExceeded);
        _scheduler.Verify(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()), Times.Never,
            "far-future slots are never parked (L3 bound)");
    }

    [Fact]
    public async Task Should_reject_with_discard_kind_when_overflow_behavior_is_discard()
    {
        var policy = new RateLimitPolicy(1, TimeSpan.FromSeconds(10))
        {
            Burst            = 1,
            OverflowBehavior = RateLimitOverflowBehavior.Discard
        };
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(2));

        var gate     = CreateGate();
        var executor = CreateExecutor(policy, "k");
        var result   = await gate.TryPassAsync(executor, CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Rejected, "Discard never waits and never parks");
        result.RejectionKind.ShouldBe(RateLimitRejectionKind.Discarded);
        _scheduler.Verify(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()), Times.Never);
        _limiter.Verify(l => l.ReleaseAsync(It.IsAny<Type>(), "k", executor.PersistenceId, It.IsAny<CancellationToken>()),
            Times.Once, "the unused reservation is released best-effort");
    }

    [Fact]
    public async Task Should_track_parked_tasks_and_release_on_enqueue()
    {
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var gate     = CreateGate();
        var executor = CreateExecutor(Policy(), "k");

        await gate.TryPassAsync(executor, CancellationToken.None);
        _parkingLot.Count.ShouldBe(1, "a deferred task is registered in the parking lot");

        // Re-park of the SAME task must not double-count (distinct tasks only)
        await gate.TryPassAsync(executor, CancellationToken.None);
        _parkingLot.Count.ShouldBe(1);

        // The enqueue notification (slot fired, task re-entered a channel) releases the slot
        _parkingLot.OnTaskEnqueued(executor.PersistenceId);
        _parkingLot.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Should_floor_past_slot_to_near_future_when_reparking()
    {
        // Past slot + MaxInSlotWait disabled: the gate must floor the re-park (anti busy-spin)
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(-5));

        TaskHandlerExecutor? parked = null;
        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), null))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((e, _) => parked = e);

        var gate   = CreateGate();
        var before = DateTimeOffset.UtcNow;
        var result = await gate.TryPassAsync(
            CreateExecutor(Policy(maxInSlotWait: TimeSpan.Zero), "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred);
        parked.ShouldNotBeNull();
        parked.ExecutionTime!.Value.ShouldBeGreaterThan(before, "a past slot must be floored into the future");
        parked.ExecutionTime!.Value.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow + gate.PastSlotFloor,
            "the floor must stay minimal (no flat clamp overshooting the GCRA slot)");
    }

    // ---------------------------------------------------------------- near slot (no in-slot wait, L14)

    [Fact]
    public async Task Should_defer_near_slot_without_inslot_wait()
    {
        // L14: a near (within MaxInSlotWait) but unavailable slot must NOT be awaited inline on the
        // consumer. The gate re-parks (Defer) and the limiter is hit EXACTLY ONCE (no in-slot
        // re-acquire loop, no Task.Delay), so a single-consumer queue is never head-of-line blocked by
        // the wait — not even tasks without any policy queued behind it. The task still fires at its
        // slot via redelivery. Pre-fix this awaited the slot inline and returned Proceed after ~2 acquires.
        var calls = 0;
        _limiter.Setup(l => l.TryAcquireAsync(It.IsAny<RateLimitPolicy>(), It.IsAny<Type>(), It.IsAny<string>(),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Interlocked.Increment(ref calls);
                    return new RateLimitDecision(false, DateTimeOffset.UtcNow.AddMilliseconds(250));
                });

        TaskHandlerExecutor? parked = null;
        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), null))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((e, _) => parked = e);

        var gate   = CreateGate();
        var result = await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred, "a near slot is re-parked, not awaited inline");
        calls.ShouldBe(1, "the limiter is acquired exactly once — no in-slot re-acquire loop (L14)");
        parked.ShouldNotBeNull("the task must be re-parked to the scheduler");
    }

    // ---------------------------------------------------------------- invalidation (set-then-check)

    [Fact]
    public async Task Should_drop_stale_registration_when_invalidated_during_repark()
    {
        var slot = DateTimeOffset.UtcNow.AddSeconds(8);
        SetupDeferral(slot);

        var executor = CreateExecutor(Policy(), "k");

        // The Cancel/re-dispatch lands while the gate is re-parking: invisible to TryUnschedule
        // (nothing parked yet), visible to the epoch check afterwards
        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), null))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((_, _) => _registry.Invalidate(executor.PersistenceId));
        _scheduler.Setup(s => s.TryUnschedule(executor.PersistenceId, It.IsAny<TaskHandlerExecutor>()))
                  .Returns(true);

        var gate   = CreateGate();
        var result = await gate.TryPassAsync(executor, CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred);
        _scheduler.Verify(s => s.TryUnschedule(executor.PersistenceId, It.IsAny<TaskHandlerExecutor>()), Times.Once,
            "the stale parked registration must be dropped (set-then-check)");
        _limiter.Verify(l => l.ReleaseAsync(It.IsAny<Type>(), "k", executor.PersistenceId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_keep_registration_when_no_invalidation_occurred()
    {
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var gate = CreateGate();
        await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None);

        _scheduler.Verify(s => s.TryUnschedule(It.IsAny<Guid>(), It.IsAny<TaskHandlerExecutor>()), Times.Never);
    }

    [Fact]
    public async Task Should_release_parking_entry_when_invalidator_already_unscheduled_the_registration()
    {
        // A Cancel or same-taskKey re-dispatch lands between the gate's Schedule and Park: its
        // own unconditional TryUnschedule already removed the registration, so the gate's
        // conditional unschedule fails — but IsScheduled == false reveals that nothing is
        // registered anymore, and the parking-lot entry must still be released, or it leaks
        // forever (the task never re-enqueues).
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var executor = CreateExecutor(Policy(), "k");

        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), null))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((_, _) => _registry.Invalidate(executor.PersistenceId));
        _scheduler.Setup(s => s.TryUnschedule(executor.PersistenceId, It.IsAny<TaskHandlerExecutor>()))
                  .Returns(false); // the invalidator's unconditional TryUnschedule won the race
        _scheduler.Setup(s => s.IsScheduled(executor.PersistenceId))
                  .Returns(false); // nothing is registered anymore

        var gate = CreateGate();
        await gate.TryPassAsync(executor, CancellationToken.None);

        _parkingLot.Count.ShouldBe(0, "an invalidated task must not leave a permanent parking-lot entry");
        _limiter.Verify(l => l.ReleaseAsync(It.IsAny<Type>(), "k", executor.PersistenceId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Should_preserve_newer_registration_when_invalidated_by_redispatch()
    {
        // Epoch moved because of a same-taskKey re-dispatch whose own deferral re-parked the
        // task (newer registration): conditional unschedule fails AND IsScheduled is true →
        // the newer parking entry and reservation must survive untouched.
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var executor = CreateExecutor(Policy(), "k");

        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), null))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((_, _) => _registry.Invalidate(executor.PersistenceId));
        _scheduler.Setup(s => s.TryUnschedule(executor.PersistenceId, It.IsAny<TaskHandlerExecutor>()))
                  .Returns(false); // a newer registration is parked
        _scheduler.Setup(s => s.IsScheduled(executor.PersistenceId))
                  .Returns(true);  // ...and it is still registered

        var gate = CreateGate();
        await gate.TryPassAsync(executor, CancellationToken.None);

        _parkingLot.Count.ShouldBe(1, "the newer registration's parking entry must survive");
        _limiter.Verify(l => l.ReleaseAsync(It.IsAny<Type>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never, "the newer registration still depends on the reservation");
    }

    // ---------------------------------------------------------------- deferral event aggregation

    [Fact]
    public async Task Should_emit_first_deferral_event_then_aggregate_within_window()
    {
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var gate = CreateGate();
        gate.DeferralEventWindow = TimeSpan.FromMinutes(5);

        var first  = await gate.TryPassAsync(CreateExecutor(Policy(), "agg-key"), CancellationToken.None);
        var second = await gate.TryPassAsync(CreateExecutor(Policy(), "agg-key"), CancellationToken.None);

        first.EmitDeferralEvent.ShouldBeTrue("the first deferral of a window emits immediately");
        first.AggregatedDeferrals.ShouldBe(1);
        second.EmitDeferralEvent.ShouldBeFalse("further deferrals in the window are aggregated");
    }

    [Fact]
    public async Task Should_emit_every_deferral_when_window_is_zero()
    {
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var gate = CreateGate();
        gate.DeferralEventWindow = TimeSpan.Zero;

        (await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None)).EmitDeferralEvent.ShouldBeTrue();
        (await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None)).EmitDeferralEvent.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_not_emit_deferral_events_when_disabled()
    {
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));
        _configuration.SetRateLimiterOptions(o => o.EmitDeferralEvents = false);

        var gate   = CreateGate();
        var result = await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred);
        result.EmitDeferralEvent.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_sweep_stale_deferral_aggregation_entries()
    {
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var gate = CreateGate();
        gate.DeferralEventWindow      = TimeSpan.FromMinutes(5);
        gate.AggregationSweepInterval = TimeSpan.Zero;
        gate.AggregationEntryTtl      = TimeSpan.Zero;

        await gate.TryPassAsync(CreateExecutor(Policy(), "sweep-a"), CancellationToken.None);
        gate.DeferralAggregationCount.ShouldBe(1);

        await Task.Delay(20); // let the clock advance past the first entry's window start

        await gate.TryPassAsync(CreateExecutor(Policy(), "sweep-b"), CancellationToken.None);
        gate.DeferralAggregationCount.ShouldBe(1,
            "the stale entry must be evicted by the opportunistic sweep (unbounded growth otherwise)");
    }

    [Fact]
    public async Task Should_keep_live_deferral_aggregation_entries_when_within_ttl()
    {
        SetupDeferral(DateTimeOffset.UtcNow.AddSeconds(8));

        var gate = CreateGate();
        gate.DeferralEventWindow      = TimeSpan.FromMinutes(5);
        gate.AggregationSweepInterval = TimeSpan.Zero;
        gate.AggregationEntryTtl      = TimeSpan.FromHours(1);

        await gate.TryPassAsync(CreateExecutor(Policy(), "live-a"), CancellationToken.None);
        await gate.TryPassAsync(CreateExecutor(Policy(), "live-b"), CancellationToken.None);

        gate.DeferralAggregationCount.ShouldBe(2, "live entries must survive the sweep");
    }

    // ---------------------------------------------------------------- fail-open event (real limiter)

    [Fact]
    public async Task Should_emit_deferred_fail_open_event_when_window_elapses_even_without_new_fail_opens()
    {
        // Guards the double-CAS fix: a fail-open observed INSIDE a closed window must not be
        // consumed silently — once the window elapses, the next Proceed must still emit it
        var options = new EverTask.RateLimiting.RateLimiterOptions { MaxTrackedKeys = 1 };
        options.ResolveDefaults(1000);
        var limiter = new InMemoryKeyedRateLimiter(options,
            new Mock<IEverTaskLogger<InMemoryKeyedRateLimiter>>().Object, sweepInterval: TimeSpan.Zero);

        var policy = new RateLimitPolicy(100, TimeSpan.FromSeconds(1)); // generous: always conforming
        var gate   = CreateGate(limiter);
        gate.DeferralEventWindow = TimeSpan.FromMinutes(5);

        // K1 becomes the single tracked key; no fail-open yet
        var first = await gate.TryPassAsync(CreateExecutor(policy, "K1"), CancellationToken.None);
        first.EmitFailOpenEvent.ShouldBeFalse();

        // K2 exceeds MaxTrackedKeys → fail-open #1 → first event emits immediately
        var second = await gate.TryPassAsync(CreateExecutor(policy, "K2"), CancellationToken.None);
        second.EmitFailOpenEvent.ShouldBeTrue();
        second.TotalFailOpenCount.ShouldBe(1);

        // K3 → fail-open #2 inside the closed window: no event, but the delta must NOT be consumed
        var third = await gate.TryPassAsync(CreateExecutor(policy, "K3"), CancellationToken.None);
        third.EmitFailOpenEvent.ShouldBeFalse();

        // Window elapses (set to zero), K1 conforms again (NO new fail-open): the pending
        // delta must still surface — with the pre-fix double-CAS it was swallowed forever
        gate.DeferralEventWindow = TimeSpan.Zero;
        var fourth = await gate.TryPassAsync(CreateExecutor(policy, "K1"), CancellationToken.None);
        fourth.EmitFailOpenEvent.ShouldBeTrue("the fail-open observed inside the closed window must not be lost");
        fourth.TotalFailOpenCount.ShouldBe(2);
    }

    // ---------------------------------------------------------------- DI defaults

    [Fact]
    public async Task Should_compute_max_parked_tasks_default_from_post_builder_channel_capacity()
    {
        // ResolveDefaults runs lazily at first resolution: the min(5000, 2 × capacity) formula
        // must see the capacity set by the BUILDER (after AddEverTask), not the initial default
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(RateLimitGateTests).Assembly))
                .ConfigureDefaultQueue(q => q.SetChannelCapacity(50))
                .AddMemoryStorage();

        await using var provider = services.BuildServiceProvider();

        var parkingLot = provider.GetRequiredService<RateLimitParkingLot>();
        parkingLot.MaxParkedTasks.ShouldBe(100, "min(5000, 2 × 50) from the post-builder capacity");
    }

    // ---------------------------------------------------------------- WorkerExecutor invariants

    private WorkerExecutor CreateWorkerExecutor(
        Mock<IServiceScopeFactory> scopeFactory,
        Mock<IRateLimitGate> gate,
        Mock<IWorkerBlacklist>? blacklist = null)
    {
        blacklist ??= new Mock<IWorkerBlacklist>();
        return new WorkerExecutor(
            blacklist.Object,
            _configuration,
            scopeFactory.Object,
            _scheduler.Object,
            new Mock<ICancellationSourceProvider>().Object,
            new Mock<IEverTaskLogger<WorkerExecutor>>().Object,
            NullLoggerFactory.Instance,
            gate.Object);
    }

    [Fact]
    public async Task Should_not_write_storage_when_deferring()
    {
        // A deferral must write NOTHING: the deferred path never enters DoWorkCore, so the
        // per-task scope (the only place storage is resolved) is never even created
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var gate         = new Mock<IRateLimitGate>();
        gate.Setup(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitGateResult(RateLimitGateOutcome.Deferred, DateTimeOffset.UtcNow.AddSeconds(5)));

        var workerExecutor = CreateWorkerExecutor(scopeFactory, gate);

        await workerExecutor.DoWork(CreateExecutor(Policy(), "k", task: new GateTaskB()), CancellationToken.None);

        scopeFactory.Verify(f => f.CreateScope(), Times.Never,
            "the Deferred path must never enter DoWorkCore (no scope, no storage write)");
    }

    [Fact]
    public async Task Should_discard_blacklisted_task_before_gate()
    {
        // Cancelled tasks must not burn rate-limit tokens: blacklist check happens BEFORE the gate
        var executor  = CreateExecutor(Policy(), "k", task: new GateTaskB());
        var blacklist = new Mock<IWorkerBlacklist>();
        blacklist.Setup(b => b.IsBlacklisted(executor.PersistenceId)).Returns(true);

        var gate           = new Mock<IRateLimitGate>(MockBehavior.Strict);
        var scopeFactory   = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var workerExecutor = CreateWorkerExecutor(scopeFactory, gate, blacklist);

        await workerExecutor.DoWork(executor, CancellationToken.None);

        gate.Verify(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()), Times.Never);
        blacklist.Verify(b => b.Remove(executor.PersistenceId), Times.Once);
        scopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    [Fact]
    public async Task Should_discard_task_when_cancelled_during_gate_waits()
    {
        // Gate waits (parking-lot pause + in-slot waits) can take seconds and the per-task
        // token does not exist yet: a Cancel landing during them only reaches the blacklist,
        // which must be re-checked after a Proceed outcome
        var executor  = CreateExecutor(Policy(), "k", task: new GateTaskB());
        var blacklist = new Mock<IWorkerBlacklist>();
        blacklist.SetupSequence(b => b.IsBlacklisted(executor.PersistenceId))
                 .Returns(false)  // entry check: not cancelled yet
                 .Returns(true);  // post-gate check: Cancel landed during the gate waits

        var gate = new Mock<IRateLimitGate>();
        gate.Setup(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitGateResult(RateLimitGateOutcome.Proceed));

        var scopeFactory   = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var workerExecutor = CreateWorkerExecutor(scopeFactory, gate, blacklist);

        await workerExecutor.DoWork(executor, CancellationToken.None);

        scopeFactory.Verify(f => f.CreateScope(), Times.Never,
            "a task cancelled during the gate waits must never execute");
        blacklist.Verify(b => b.Remove(executor.PersistenceId), Times.Once);
    }

    [Fact]
    public async Task Should_not_clobber_cancelled_status_when_cancelled_during_gate_waits_and_outcome_is_rejected()
    {
        // Cancel landing during the gate waits with a REJECTED outcome: the rejection handling
        // (SetStatus Failed + OnError) must be skipped entirely, or the user's persisted
        // Cancelled status would be overwritten with Failed. No scope = no storage write.
        var executor  = CreateExecutor(Policy(), "k", task: new GateTaskB());
        var blacklist = new Mock<IWorkerBlacklist>();
        blacklist.SetupSequence(b => b.IsBlacklisted(executor.PersistenceId))
                 .Returns(false)  // entry check: not cancelled yet
                 .Returns(true);  // post-gate check: Cancel landed during the gate waits

        var gate = new Mock<IRateLimitGate>();
        gate.Setup(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitGateResult(RateLimitGateOutcome.Rejected,
                DateTimeOffset.UtcNow.AddHours(2), RejectionKind: RateLimitRejectionKind.HorizonExceeded));

        var scopeFactory   = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var workerExecutor = CreateWorkerExecutor(scopeFactory, gate, blacklist);

        await workerExecutor.DoWork(executor, CancellationToken.None);

        scopeFactory.Verify(f => f.CreateScope(), Times.Never,
            "the rejection of a cancelled task must not write Failed nor invoke OnError");
        blacklist.Verify(b => b.Remove(executor.PersistenceId), Times.Once);
    }

    [Fact]
    public async Task Should_not_touch_gate_for_tasks_without_policy()
    {
        // L0 fast path + L2 scoping: a task WITHOUT a RateLimitPolicy must never touch the
        // gate — not even WaitForParkingCapacityAsync (an over-cap lot would otherwise pause
        // unrelated traffic for up to MaxOverflowPause per task)
        var executor = CreateExecutor(policy: null, key: null, task: new GateTaskE()) with
        {
            Handler         = null,
            HandlerTypeName = typeof(NoopGateTaskEHandler).AssemblyQualifiedName
        };

        var services = new ServiceCollection();
        services.AddTransient<NoopGateTaskEHandler>();
        services.AddSingleton<IGuidGenerator>(new DefaultGuidGenerator(UUIDNext.Database.Other));
        await using var provider = services.BuildServiceProvider();

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(() => provider.CreateScope());

        var gate           = new Mock<IRateLimitGate>(MockBehavior.Strict);
        var workerExecutor = CreateWorkerExecutor(scopeFactory, gate);

        await workerExecutor.DoWork(executor, CancellationToken.None);

        gate.VerifyNoOtherCalls();
        scopeFactory.Verify(f => f.CreateScope(), Times.Once, "the task itself must still execute");
    }

    public sealed record GateTaskE : IEverTask;

    public sealed class NoopGateTaskEHandler : EverTaskHandler<GateTaskE>
    {
        public override Task Handle(GateTaskE backgroundTask, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Should_repark_gated_redelivery_when_original_delivery_still_in_flight()
    {
        // A retry-deferral redelivery overlapping the original delivery's unwind must NOT
        // touch the gate (it would redeem the reservation) and must NOT be dropped (the only
        // live copy would be stranded until restart): it is re-parked at a short delay.
        var executor = CreateExecutor(Policy(), "k", task: new GateTaskB()) with
        {
            Handler         = null, // lazy: the worker resolves the blocking handler below
            HandlerTypeName = typeof(BlockingGateTaskBHandler).AssemblyQualifiedName
        };

        var gate = new Mock<IRateLimitGate>();
        gate.Setup(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitGateResult(RateLimitGateOutcome.Proceed));

        // First delivery: enters DoWorkCore (in-flight) and blocks there inside the handler
        var coordinator  = new BlockingHandlerCoordinator(new TaskCompletionSource(), new TaskCompletionSource());
        var scopeFactory = CreateBlockingScopeFactory(coordinator);

        var workerExecutor = CreateWorkerExecutor(scopeFactory, gate);
        var firstDelivery  = workerExecutor.DoWork(executor, CancellationToken.None).AsTask();
        await coordinator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Redelivery while the original is in flight
        await workerExecutor.DoWork(executor, CancellationToken.None);

        gate.Verify(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()), Times.Once,
            "the redelivery must not consume the gate while the original is in flight");
        gate.Verify(g => g.ReparkInFlightRedelivery(
                It.Is<TaskHandlerExecutor>(e => e.PersistenceId == executor.PersistenceId)), Times.Once,
            "the redelivery must be re-parked, not dropped");

        coordinator.Release.SetResult();
        await firstDelivery.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // -------------------------------------------- gate-level in-flight re-park mechanics

    [Fact]
    public void Should_repark_one_shot_redelivery_at_configured_delay_and_register_parking_entry()
    {
        var executor = CreateExecutor(Policy(), "k");

        TaskHandlerExecutor? reparked = null;
        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), null))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((e, _) => reparked = e);

        var gate = CreateGate();
        gate.InFlightRedeliveryDelay = TimeSpan.FromMilliseconds(750);

        var before = DateTimeOffset.UtcNow;
        gate.ReparkInFlightRedelivery(executor);

        reparked.ShouldNotBeNull();
        reparked.IsLazy.ShouldBeTrue();
        reparked.ExecutionTime.ShouldNotBeNull();
        reparked.ExecutionTime!.Value.ShouldBeGreaterThanOrEqualTo(before + TimeSpan.FromMilliseconds(750),
            "the configured delay must actually be applied (no hot re-park loop)");
        _parkingLot.Count.ShouldBe(1, "the re-park must re-register the L2 parking-lot entry");
    }

    [Fact]
    public void Should_repark_recurring_redelivery_via_next_run_without_touching_execution_time()
    {
        var occurrenceTime = DateTimeOffset.UtcNow.AddSeconds(-2);
        var recurring      = new RecurringTask { SecondInterval = new SecondInterval(30) };
        var executor       = CreateExecutor(Policy(), "k", recurring: recurring, executionTime: occurrenceTime);

        TaskHandlerExecutor? reparked = null;
        DateTimeOffset? nextRecurringRun = null;
        _scheduler.Setup(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()))
                  .Callback<TaskHandlerExecutor, DateTimeOffset?>((e, n) =>
                  {
                      reparked         = e;
                      nextRecurringRun = n;
                  });

        var gate = CreateGate();
        gate.InFlightRedeliveryDelay = TimeSpan.FromMilliseconds(500);

        var before = DateTimeOffset.UtcNow;
        gate.ReparkInFlightRedelivery(executor);

        reparked.ShouldNotBeNull();
        reparked.IsLazy.ShouldBeTrue();
        nextRecurringRun.ShouldNotBeNull("recurring re-park must schedule via nextRecurringRun");
        nextRecurringRun!.Value.ShouldBeGreaterThanOrEqualTo(before + TimeSpan.FromMilliseconds(500));
        reparked.ExecutionTime.ShouldBe(occurrenceTime,
            "ExecutionTime must keep pointing at the occurrence's scheduled time (schedule-drift rule)");
    }

    [Fact]
    public void Should_not_repark_recurring_redelivery_past_run_until()
    {
        // F14: ReparkInFlightRedelivery must apply the same RunUntil guard as the Defer path — an
        // in-flight redelivery whose re-park slot falls past RunUntil must be DROPPED (never fired late),
        // not re-parked.
        var recurring = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            RunUntil       = DateTimeOffset.UtcNow.AddMilliseconds(200) // the +1 s re-park slot exceeds this
        };
        var executor = CreateExecutor(Policy(), "k", recurring: recurring, executionTime: DateTimeOffset.UtcNow);

        var gate = CreateGate();
        gate.InFlightRedeliveryDelay = TimeSpan.FromSeconds(1);

        gate.ReparkInFlightRedelivery(executor);

        _scheduler.Verify(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()), Times.Never,
            "an in-flight redelivery whose re-park slot falls past RunUntil must be dropped, not re-parked");
        _parkingLot.Count.ShouldBe(0, "a dropped redelivery must not register a parking-lot entry");
    }

    [Fact]
    public void Should_not_overwrite_existing_registration_when_reparking_redelivery()
    {
        // A newer registration (e.g. a re-dispatched payload already re-parked) must never be
        // overwritten by a stale redelivery's re-park: Schedule is latest-wins
        var executor = CreateExecutor(Policy(), "k");
        _scheduler.Setup(s => s.IsScheduled(executor.PersistenceId)).Returns(true);

        var gate = CreateGate();
        gate.ReparkInFlightRedelivery(executor);

        _scheduler.Verify(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()), Times.Never,
            "an existing registration must survive (latest payload wins)");
        _parkingLot.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Should_skip_occurrence_without_failing_series_when_retry_is_terminally_rejected_for_recurring_task()
    {
        // Retry-path terminal rejection of a RECURRING task must have the same semantics as the
        // pre-execution rejection: occurrence skipped (no Failed, no OnError), series advanced
        // via QueueNextOccourrence, status back to Queued.
        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.GetCurrentRunCount(It.IsAny<Guid>())).ReturnsAsync(0);

        var services = new ServiceCollection();
        services.AddSingleton(storage.Object);
        services.AddTransient<AlwaysFailingRecurringHandler>();
        services.AddSingleton<IGuidGenerator>(new DefaultGuidGenerator(UUIDNext.Database.Other));
        await using var provider = services.BuildServiceProvider();

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(() => provider.CreateScope());

        var gate = new Mock<IRateLimitGate>();
        gate.SetupSequence(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitGateResult(RateLimitGateOutcome.Proceed))     // delivery pass
            .ReturnsAsync(new RateLimitGateResult(RateLimitGateOutcome.Rejected,     // retry re-acquire
                DateTimeOffset.UtcNow.AddHours(2), RejectionKind: RateLimitRejectionKind.HorizonExceeded));

        var recurring = new RecurringTask { SecondInterval = new SecondInterval(30) };
        var executor = CreateExecutor(Policy(), "k", task: new GateTaskD(),
            recurring: recurring, executionTime: DateTimeOffset.UtcNow) with
        {
            Handler         = null,
            HandlerTypeName = typeof(AlwaysFailingRecurringHandler).AssemblyQualifiedName
        };

        var workerExecutor = CreateWorkerExecutor(scopeFactory, gate);
        await workerExecutor.DoWork(executor, CancellationToken.None);

        storage.Verify(s => s.SetStatus(It.IsAny<Guid>(), QueuedTaskStatus.Failed, It.IsAny<Exception?>(),
                It.IsAny<AuditLevel>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never, "a rejected retry of a recurring task must never fail the series");
        storage.Verify(s => s.SetQueued(executor.PersistenceId, It.IsAny<AuditLevel>(), It.IsAny<CancellationToken>()),
            Times.Once, "the skipped occurrence returns to Queued like any parked occurrence");
        storage.Verify(s => s.UpdateCurrentRun(executor.PersistenceId, It.IsAny<double>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<AuditLevel>(), It.IsAny<int>()),
            Times.Once, "the series must advance through the normal next-occurrence path");
    }

    public sealed record GateTaskD : IEverTask;

    public sealed class AlwaysFailingRecurringHandler : EverTaskHandler<GateTaskD>
    {
        public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(20));

        public override Task Handle(GateTaskD backgroundTask, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("transient failure forcing a retry");
    }

    public sealed record BlockingHandlerCoordinator(TaskCompletionSource Entered, TaskCompletionSource Release);

    /// <summary>
    /// Scope factory whose resolved handler blocks inside Handle until released: used to hold a
    /// delivery in flight deterministically.
    /// </summary>
    private static Mock<IServiceScopeFactory> CreateBlockingScopeFactory(BlockingHandlerCoordinator coordinator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(coordinator);
        services.AddTransient<BlockingGateTaskBHandler>();
        services.AddSingleton<IGuidGenerator>(new DefaultGuidGenerator(UUIDNext.Database.Other));
        var provider = services.BuildServiceProvider();

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope())
                    .Returns(() => provider.CreateScope());
        return scopeFactory;
    }

    public sealed class BlockingGateTaskBHandler(BlockingHandlerCoordinator coordinator)
        : EverTaskHandler<GateTaskB>
    {
        public override async Task Handle(GateTaskB backgroundTask, CancellationToken cancellationToken)
        {
            coordinator.Entered.TrySetResult();
            await coordinator.Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    // ---------------------------------------------------------------- wrapper extraction & ToLazy threading

    private static IServiceProvider CreateWrapperProvider()
    {
        var services = new ServiceCollection();
        services.AddTransient<IEverTaskHandler<RateLimitedWrapperTask>, RateLimitedWrapperTaskHandler>();
        services.AddTransient<IEverTaskHandler<RateLimitedThrowingKeyTask>, RateLimitedThrowingKeyTaskHandler>();
        services.AddSingleton<IGuidGenerator>(new DefaultGuidGenerator(UUIDNext.Database.Other));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Should_stamp_policy_and_key_on_eager_executor()
    {
        var wrapper  = new TaskHandlerWrapperImp<RateLimitedWrapperTask>();
        var executor = await wrapper.Handle(new RateLimitedWrapperTask("tenant-42"), null, null,
            CreateWrapperProvider(), AuditLevel.Full);

        executor.RateLimitPolicy.ShouldBe(RateLimitedWrapperTaskHandler.DeclaredPolicy,
            "the policy is extracted once per handler type (first-wins cache)");
        executor.RateLimitKey.ShouldBe("tenant-42", "the key is derived per dispatch from the task");
    }

    [Fact]
    public async Task Should_stamp_policy_and_key_on_lazy_executor()
    {
        var wrapper  = new TaskHandlerWrapperImp<RateLimitedWrapperTask>();
        var executor = await wrapper.Handle(new RateLimitedWrapperTask("tenant-7"), null, null,
            CreateWrapperProvider(), AuditLevel.Full, useLazyExecutor: true);

        executor.IsLazy.ShouldBeTrue();
        executor.RateLimitPolicy.ShouldBe(RateLimitedWrapperTaskHandler.DeclaredPolicy);
        executor.RateLimitKey.ShouldBe("tenant-7");
    }

    [Fact]
    public async Task Should_proceed_ungated_when_key_selector_throws()
    {
        var wrapper  = new TaskHandlerWrapperImp<RateLimitedThrowingKeyTask>();
        var executor = await wrapper.Handle(new RateLimitedThrowingKeyTask(), null, null,
            CreateWrapperProvider(), AuditLevel.Full, useLazyExecutor: true);

        executor.RateLimitPolicy.ShouldNotBeNull();
        executor.RateLimitKey.ShouldBeNull("a broken key selector is fail-safe: the task executes ungated");
    }

    [Fact]
    public void Should_preserve_rate_limit_fields_in_ToLazy_for_both_branches()
    {
        var policy = Policy();

        // Eager → lazy
        var eager = CreateExecutor(policy, "k");
        var lazy  = eager.ToLazy() with { HandlerTypeName = typeof(object).AssemblyQualifiedName };
        lazy.RateLimitPolicy.ShouldBe(policy);
        lazy.RateLimitKey.ShouldBe("k");

        // Already lazy → new lazy instance
        var relazied = lazy.ToLazy();
        relazied.RateLimitPolicy.ShouldBe(policy);
        relazied.RateLimitKey.ShouldBe("k");
        ReferenceEquals(relazied, lazy).ShouldBeFalse();
    }
}

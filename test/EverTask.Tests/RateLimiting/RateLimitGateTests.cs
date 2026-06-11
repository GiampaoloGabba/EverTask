using EverTask.Handler;
using EverTask.Logger;
using EverTask.RateLimiting;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
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
    private sealed record GateTaskB : IEverTask;
    private sealed record GateTaskEmptyKey : IEverTask;

    private readonly Mock<IKeyedRateLimiter> _limiter   = new();
    private readonly Mock<IScheduler> _scheduler        = new();
    private readonly GateInvalidationRegistry _registry = new();
    private readonly EverTaskServiceConfiguration _configuration = new();

    private RateLimitGate CreateGate() =>
        new(_limiter.Object, _scheduler.Object, _registry, _configuration,
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

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred, "the occurrence must not execute");
        _scheduler.Verify(s => s.Schedule(It.IsAny<TaskHandlerExecutor>(), It.IsAny<DateTimeOffset?>()), Times.Never,
            "an occurrence past RunUntil is dropped, never fired late");
        _limiter.Verify(l => l.ReleaseAsync(It.IsAny<Type>(), "k", executor.PersistenceId, It.IsAny<CancellationToken>()),
            Times.Once, "the unredeemable reservation is released best-effort");
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

    // ---------------------------------------------------------------- in-slot wait

    [Fact]
    public async Task Should_wait_in_slot_and_proceed_when_slot_is_near()
    {
        var calls = 0;
        _limiter.Setup(l => l.TryAcquireAsync(It.IsAny<RateLimitPolicy>(), It.IsAny<Type>(), It.IsAny<string>(),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++calls == 1
                    ? new RateLimitDecision(false, DateTimeOffset.UtcNow.AddMilliseconds(250))
                    : new RateLimitDecision(true, default));

        var gate    = CreateGate();
        var started = DateTimeOffset.UtcNow;
        var result  = await gate.TryPassAsync(CreateExecutor(Policy(), "k"), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Proceed, "a near slot is awaited inline and redeemed");
        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150),
            "the gate must actually wait for the slot (lower-bound with tolerance)");
        _scheduler.VerifyNoOtherCalls();
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

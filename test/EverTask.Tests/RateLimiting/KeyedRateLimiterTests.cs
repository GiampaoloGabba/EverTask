using EverTask.Logger;
using EverTask.RateLimiting;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.RateLimiting;

/// <summary>
/// Unit tests for <see cref="InMemoryKeyedRateLimiter"/> GCRA math, reservation/redemption
/// semantics, isolation, eviction and bounds. Fake clock, zero sleeps: every assertion is exact.
/// </summary>
public class KeyedRateLimiterTests
{
    private sealed record TaskA : IEverTask;
    private sealed record TaskB : IEverTask;

    private readonly FakeTimeProvider _clock = new();

    private InMemoryKeyedRateLimiter CreateLimiter(RateLimiterOptions? options = null) =>
        new(options ?? new RateLimiterOptions(),
            new Mock<IEverTaskLogger<InMemoryKeyedRateLimiter>>().Object,
            _clock,
            sweepInterval: TimeSpan.Zero); // sweeps triggered manually in tests

    private static RateLimitPolicy Policy(int permits, TimeSpan period, int? burst = null, bool startEmpty = false,
                                          TimeSpan? horizon = null) =>
        new(permits, period)
        {
            Burst                 = burst ?? permits,
            StartEmpty            = startEmpty,
            MaxReservationHorizon = horizon ?? TimeSpan.FromHours(1)
        };

    private async Task<RateLimitDecision> Acquire(InMemoryKeyedRateLimiter limiter, RateLimitPolicy policy,
                                                  string key = "tenant-1", Guid? id = null, Type? taskType = null) =>
        await limiter.TryAcquireAsync(policy, taskType ?? typeof(TaskA), key, id ?? Guid.NewGuid());

    // ---------------------------------------------------------------- GCRA math

    [Fact]
    public async Task Should_allow_full_burst_then_defer_when_budget_exhausted()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 15, period: TimeSpan.FromMinutes(1), burst: 15);

        for (var i = 0; i < 15; i++)
            (await Acquire(limiter, policy)).Acquired.ShouldBeTrue($"acquire #{i + 1} is within the burst");

        var deferred = await Acquire(limiter, policy);
        deferred.Acquired.ShouldBeFalse("the 16th acquire exceeds the burst");

        // First deferred slot is exactly one emission interval (T = 60s/15 = 4s) from now
        deferred.RetryAt.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Should_space_consecutive_deferred_slots_by_emission_interval()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 6, period: TimeSpan.FromMinutes(1), burst: 1);
        var t       = TimeSpan.FromSeconds(10); // T = 60s/6

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var previous = default(DateTimeOffset?);
        for (var i = 0; i < 5; i++)
        {
            var decision = await Acquire(limiter, policy);
            decision.Acquired.ShouldBeFalse();

            if (previous.HasValue)
                (decision.RetryAt - previous.Value).ShouldBe(t, "slots are spaced exactly T apart");

            previous = decision.RetryAt;
        }
    }

    [Fact]
    public async Task Should_never_exceed_permits_in_any_sliding_window_when_burst_is_one()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 4, period: TimeSpan.FromSeconds(40), burst: 1); // T = 10s

        var acquiredAt = new List<DateTimeOffset>();

        // Greedy client: tries every second for 2 minutes, executes whenever allowed
        for (var i = 0; i < 120; i++)
        {
            if ((await Acquire(limiter, policy)).Acquired)
                acquiredAt.Add(_clock.GetUtcNow());
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        acquiredAt.ShouldNotBeEmpty();

        // Strict sliding-window invariant (Burst=1): any window of length Period contains at
        // most Permits executions
        foreach (var start in acquiredAt)
        {
            var inWindow = acquiredAt.Count(at => at >= start && at < start + policy.Period);
            inWindow.ShouldBeLessThanOrEqualTo(policy.Permits);
        }
    }

    [Fact]
    public async Task Should_refill_full_burst_after_idle_period()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 5, period: TimeSpan.FromSeconds(50), burst: 5);

        for (var i = 0; i < 5; i++)
            (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy)).Acquired.ShouldBeFalse();

        // After a full idle Period the bucket is full again
        _clock.Advance(policy.Period + TimeSpan.FromSeconds(10));

        for (var i = 0; i < 5; i++)
            (await Acquire(limiter, policy)).Acquired.ShouldBeTrue($"burst should be fully refilled (acquire #{i + 1})");
        (await Acquire(limiter, policy)).Acquired.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_admit_at_steady_rate_from_first_acquire_when_StartEmpty()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 10, period: TimeSpan.FromSeconds(100), burst: 10, startEmpty: true); // T = 10s

        // No accumulated burst: one steady-rate admission, then deferral at +T
        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var deferred = await Acquire(limiter, policy);
        deferred.Acquired.ShouldBeFalse("StartEmpty buckets have no burst to absorb a second immediate acquire");
        deferred.RetryAt.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(10));
    }

    // ---------------------------------------------------------------- isolation

    [Fact]
    public async Task Should_isolate_budgets_between_keys()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromMinutes(1), burst: 1);

        (await Acquire(limiter, policy, key: "tenant-1")).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy, key: "tenant-1")).Acquired.ShouldBeFalse("tenant-1 budget is exhausted");

        (await Acquire(limiter, policy, key: "tenant-2")).Acquired.ShouldBeTrue("tenant-2 has its own budget");
    }

    [Fact]
    public async Task Should_isolate_budgets_between_task_types_sharing_the_same_key()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromMinutes(1), burst: 1);

        (await Acquire(limiter, policy, taskType: typeof(TaskA))).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy, taskType: typeof(TaskA))).Acquired.ShouldBeFalse();

        (await Acquire(limiter, policy, taskType: typeof(TaskB))).Acquired.ShouldBeTrue(
            "buckets are scoped per (task type, key)");
    }

    // ---------------------------------------------------------------- reservation & redemption

    [Fact]
    public async Task Should_redeem_reservation_without_consuming_extra_capacity()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1); // T = 10s
        var start   = _clock.GetUtcNow();

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var reservationId = Guid.NewGuid();
        var deferred      = await Acquire(limiter, policy, id: reservationId);
        deferred.Acquired.ShouldBeFalse();
        deferred.RetryAt.ShouldBe(start + TimeSpan.FromSeconds(10));

        // At the reserved slot: redemption succeeds...
        _clock.Advance(TimeSpan.FromSeconds(10));
        (await Acquire(limiter, policy, id: reservationId)).Acquired.ShouldBeTrue("the slot was reserved for this id");

        // ...and consumed NO extra capacity: a third task gets the slot already promised by the
        // TAT at reserve time (start + 20s), not one interval later
        var next = await Acquire(limiter, policy, id: Guid.NewGuid());
        next.Acquired.ShouldBeFalse();
        next.RetryAt.ShouldBe(start + TimeSpan.FromSeconds(20),
            "redemption must not double-consume capacity");
    }

    [Fact]
    public async Task Should_return_same_slot_when_redelivered_before_reserved_slot()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1);

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var reservationId = Guid.NewGuid();
        var first         = await Acquire(limiter, policy, id: reservationId);
        first.Acquired.ShouldBeFalse();

        // Early redelivery (guard overlap): same slot, idempotent
        _clock.Advance(TimeSpan.FromSeconds(3));
        var redelivered = await Acquire(limiter, policy, id: reservationId);
        redelivered.Acquired.ShouldBeFalse();
        redelivered.RetryAt.ShouldBe(first.RetryAt);

        limiter.GetReservationCount(typeof(TaskA), "tenant-1").ShouldBe(1,
            "early redelivery must not book a second reservation");
    }

    [Fact]
    public async Task Should_return_non_decreasing_slots_for_distinct_reservations()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 2, period: TimeSpan.FromSeconds(20), burst: 1);

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var previous = default(DateTimeOffset?);
        for (var i = 0; i < 10; i++)
        {
            var decision = await Acquire(limiter, policy);
            decision.Acquired.ShouldBeFalse();

            if (previous.HasValue)
                decision.RetryAt.ShouldBeGreaterThanOrEqualTo(previous.Value);

            previous = decision.RetryAt;
        }
    }

    [Fact]
    public async Task Should_lapse_unredeemed_reservations_after_ttl()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1);

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var orphanId = Guid.NewGuid();
        var deferred = await Acquire(limiter, policy, id: orphanId);
        deferred.Acquired.ShouldBeFalse();
        limiter.GetReservationCount(typeof(TaskA), "tenant-1").ShouldBe(1);

        // Past slot + period + margin the orphan lapses (purged on next access)
        _clock.Advance(TimeSpan.FromSeconds(10 + 10) + limiter.ReservationExpiryMargin + TimeSpan.FromSeconds(1));
        await Acquire(limiter, policy, id: Guid.NewGuid());

        limiter.GetReservationCount(typeof(TaskA), "tenant-1").ShouldBe(0,
            "expired orphan reservations are purged opportunistically");
    }

    // ---------------------------------------------------------------- release

    [Fact]
    public async Task Should_rollback_budget_when_releasing_newest_reservation()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1);
        var start   = _clock.GetUtcNow();

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var newestId = Guid.NewGuid();
        var deferred = await Acquire(limiter, policy, id: newestId);
        deferred.RetryAt.ShouldBe(start + TimeSpan.FromSeconds(10));

        // Releasing the NEWEST reservation restores its slot for the next acquirer
        await limiter.ReleaseAsync(typeof(TaskA), "tenant-1", newestId);

        var next = await Acquire(limiter, policy, id: Guid.NewGuid());
        next.Acquired.ShouldBeFalse();
        next.RetryAt.ShouldBe(start + TimeSpan.FromSeconds(10), "the released newest slot is re-assignable");
    }

    [Fact]
    public async Task Should_not_rollback_budget_when_releasing_non_newest_reservation()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1);
        var start   = _clock.GetUtcNow();

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var olderId = Guid.NewGuid();
        (await Acquire(limiter, policy, id: olderId)).RetryAt.ShouldBe(start + TimeSpan.FromSeconds(10));
        (await Acquire(limiter, policy, id: Guid.NewGuid())).RetryAt.ShouldBe(start + TimeSpan.FromSeconds(20));

        // Releasing a NON-newest reservation must not re-open capacity already promised to the
        // later reservation: its slot simply lapses (under-use, never violation)
        await limiter.ReleaseAsync(typeof(TaskA), "tenant-1", olderId);

        var next = await Acquire(limiter, policy, id: Guid.NewGuid());
        next.RetryAt.ShouldBe(start + TimeSpan.FromSeconds(30),
            "no general rollback: the freed interval is wasted, not re-assigned");
    }

    // ---------------------------------------------------------------- horizon

    [Fact]
    public async Task Should_not_book_capacity_for_slots_beyond_reservation_horizon()
    {
        var limiter = CreateLimiter();
        var policy = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1,
            horizon: TimeSpan.FromSeconds(25));

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy)).RetryAt.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(10));
        (await Acquire(limiter, policy)).RetryAt.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(20));

        // The next slot (+30s) exceeds the 25s horizon: deferred decision WITHOUT a booking
        var beyond = await Acquire(limiter, policy, id: Guid.NewGuid());
        beyond.Acquired.ShouldBeFalse();
        beyond.RetryAt.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(30));

        limiter.GetReservationCount(typeof(TaskA), "tenant-1").ShouldBe(2,
            "slots beyond the horizon must not grow per-key state");

        // The TAT is untouched: the next acquirer is offered the same slot
        (await Acquire(limiter, policy, id: Guid.NewGuid())).RetryAt
            .ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(30));
    }

    // ---------------------------------------------------------------- eviction

    [Fact]
    public async Task Should_evict_idle_keys_and_preserve_active_ones()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1);

        // idle-key: budget consumed, then fully recovered
        (await Acquire(limiter, policy, key: "idle-key")).Acquired.ShouldBeTrue();

        // active-key: holds an outstanding reservation
        (await Acquire(limiter, policy, key: "active-key")).Acquired.ShouldBeTrue();
        var parked = Guid.NewGuid();
        (await Acquire(limiter, policy, key: "active-key", id: parked)).Acquired.ShouldBeFalse();

        limiter.TrackedKeyCount.ShouldBe(2);

        // 15s later: idle-key TAT is in the past (evictable); active-key reservation expiry
        // (slot 10s + period 10s + margin 5s = 25s) has NOT lapsed → must survive
        _clock.Advance(TimeSpan.FromSeconds(15));
        limiter.SweepIdleKeys();

        limiter.TrackedKeyCount.ShouldBe(1, "idle keys are evicted, keys with reservations are not");
        limiter.GetReservationCount(typeof(TaskA), "active-key").ShouldBe(1);

        // The evicted key behaves like a fresh bucket
        (await Acquire(limiter, policy, key: "idle-key")).Acquired.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_acquire_fresh_bucket_after_eviction()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 1, period: TimeSpan.FromSeconds(10), burst: 1);

        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(11));
        limiter.SweepIdleKeys();
        limiter.TrackedKeyCount.ShouldBe(0);

        // Re-acquire after eviction: behavior-preserving (a fresh bucket grants the same budget
        // the idle key would have granted)
        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy)).Acquired.ShouldBeFalse();
        limiter.TrackedKeyCount.ShouldBe(1);
    }

    // ---------------------------------------------------------------- concurrency (frozen clock)

    [Fact]
    public async Task Should_grant_budget_exactly_once_per_permit_under_concurrency()
    {
        var limiter = CreateLimiter();
        var policy  = Policy(permits: 5, period: TimeSpan.FromSeconds(50), burst: 5);

        // Frozen clock: 40 concurrent acquires on one key with burst 5 must produce EXACTLY
        // 5 grants; every other outcome is a deferral with a distinct, T-spaced slot
        var decisions = new RateLimitDecision[40];
        await Parallel.ForAsync(0, 40, async (i, _) =>
        {
            decisions[i] = await limiter.TryAcquireAsync(policy, typeof(TaskA), "hot-key", Guid.NewGuid());
        });

        decisions.Count(d => d.Acquired).ShouldBe(5, "the budget must be granted exactly once per permit");

        var slots = decisions.Where(d => !d.Acquired).Select(d => d.RetryAt).OrderBy(s => s).ToArray();
        slots.Length.ShouldBe(35);
        slots.Distinct().Count().ShouldBe(35, "concurrent deferrals must never double-book a slot");

        for (var i = 1; i < slots.Length; i++)
            (slots[i] - slots[i - 1]).ShouldBe(TimeSpan.FromSeconds(10), "slots are spaced exactly T apart");
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public async Task Should_fail_open_when_tracked_keys_cap_reached()
    {
        var limiter = CreateLimiter(new RateLimiterOptions { MaxTrackedKeys = 2 });
        var policy  = Policy(permits: 1, period: TimeSpan.FromMinutes(1), burst: 1);

        (await Acquire(limiter, policy, key: "key-1")).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy, key: "key-2")).Acquired.ShouldBeTrue();
        limiter.TrackedKeyCount.ShouldBe(2);

        // A third key fails OPEN: every acquire succeeds, nothing is tracked
        (await Acquire(limiter, policy, key: "key-3")).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy, key: "key-3")).Acquired.ShouldBeTrue(
            "untracked keys are not throttled (fail-open)");

        limiter.TrackedKeyCount.ShouldBe(2);
        limiter.FailOpenCount.ShouldBe(2);

        // Existing keys keep being throttled normally
        (await Acquire(limiter, policy, key: "key-1")).Acquired.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_hash_keys_longer_than_max_length_preserving_identity()
    {
        var limiter = CreateLimiter(new RateLimiterOptions { MaxKeyLength = 16 });
        var policy  = Policy(permits: 1, period: TimeSpan.FromMinutes(1), burst: 1);

        var longKey      = new string('a', 100);
        var otherLongKey = new string('a', 99) + "b";

        // The same long key maps to the same bucket (budget shared)...
        (await Acquire(limiter, policy, key: longKey)).Acquired.ShouldBeTrue();
        (await Acquire(limiter, policy, key: longKey)).Acquired.ShouldBeFalse();

        // ...while a different long key gets its own bucket
        (await Acquire(limiter, policy, key: otherLongKey)).Acquired.ShouldBeTrue();
    }

    [Fact]
    public void Should_not_exceed_maxtrackedkeys_under_concurrent_distinct_acquisitions()
    {
        // CU20: N concurrent acquisitions of N distinct NEW keys must not overshoot the cap. The
        // OnBeforeKeyAdd seam parks every acquirer past new-key detection until all are there, so they
        // all observe Count < cap before any add — the exact race. [UNIT-necessario: race not pilotable end-to-end]
        const int cap     = 4;
        const int threads = 16;

        var limiter = CreateLimiter(new RateLimiterOptions { MaxTrackedKeys = cap });
        var policy  = Policy(permits: 1, period: TimeSpan.FromMinutes(1), burst: 1);

        using var barrier = new Barrier(threads);
        limiter.OnBeforeKeyAdd = () => barrier.SignalAndWait();

        // Explicit threads (not the thread pool) so all `threads` participants reach the barrier
        // simultaneously — a smaller pool would deadlock the barrier.
        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            var key = $"key-{i}";
            workers[i] = new Thread(() =>
                limiter.TryAcquireAsync(policy, typeof(TaskA), key, Guid.NewGuid()).GetAwaiter().GetResult());
        }
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();

        limiter.TrackedKeyCount.ShouldBeLessThanOrEqualTo(cap,
            $"concurrent distinct new keys must never overshoot MaxTrackedKeys; tracked " +
            $"{limiter.TrackedKeyCount} (cap {cap}) (CU20)");
        limiter.FailOpenCount.ShouldBe(threads - cap,
            "every over-cap acquisition must fail open exactly once");
    }

    [Fact]
    public async Task Should_redeem_reservation_after_realistic_congested_redelivery_latency()
    {
        // L22: under congestion the redelivery latency (scheduler tick + full-queue retry +
        // parking-lot pause) can exceed the reservation's expiry margin. The owner must still redeem
        // the slot it booked, never re-book budget (double consumption / over-throttling).
        var limiter = CreateLimiter();
        limiter.ReservationExpiryMargin = TimeSpan.FromSeconds(5); // pin the pre-fix margin for a deterministic window
        var policy = Policy(permits: 1, period: TimeSpan.FromSeconds(1), burst: 1); // short Period

        // Exhaust the bucket so the next acquire reserves a slot.
        (await Acquire(limiter, policy)).Acquired.ShouldBeTrue();

        var id       = Guid.NewGuid();
        var deferred = await Acquire(limiter, policy, id: id);
        deferred.Acquired.ShouldBeFalse();
        limiter.GetReservationCount(typeof(TaskA), "tenant-1").ShouldBe(1);

        // Congested redelivery latency, well beyond the pinned 5 s margin:
        // Period (1 s) + retry/tick budget (5 s) + parking-lot pause (5 s).
        _clock.Advance(policy.Period + TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(5));

        // The redelivery (same id): must redeem the reserved slot.
        var redelivered = await Acquire(limiter, policy, id: id);
        redelivered.Acquired.ShouldBeTrue("the owner must redeem its reserved slot despite congested latency (L22)");
        limiter.GetReservationCount(typeof(TaskA), "tenant-1").ShouldBe(0, "the reservation was redeemed, not re-booked");

        // No double consumption: redemption did not advance the TAT, so a fresh task at this instant
        // still finds budget. Pre-fix the re-book advanced the TAT and this fresh task was deferred.
        var fresh = await Acquire(limiter, policy, id: Guid.NewGuid());
        fresh.Acquired.ShouldBeTrue("redemption must not double-consume budget (L22)");
    }
}

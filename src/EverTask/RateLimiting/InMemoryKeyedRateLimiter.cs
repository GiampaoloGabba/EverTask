using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EverTask.RateLimiting;

/// <summary>
/// In-memory, single-instance GCRA rate limiter with per-key reservation and idempotent
/// redemption. Default implementation of <see cref="IKeyedRateLimiter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>GCRA math</strong>: the steady emission interval is <c>T = Period / Permits</c> and
/// the burst tolerance is <c>tau = T × (Burst − 1)</c>. A key tracks its theoretical arrival
/// time (TAT): an acquisition conforms when <c>now ≥ TAT − tau</c>; otherwise the next slot is
/// <c>TAT − tau</c> and is booked as a reservation keyed by the task's persistence id, so a
/// redelivery of the same task redeems the same slot instead of consuming new budget.
/// </para>
/// <para>
/// <strong>Locking</strong>: TAT and the reservation map of a key mutate atomically under a
/// per-key lock (a lone CAS on TAT would let concurrent acquires double-book the reserved
/// interval). Hold time is nanosecond-scale; contention is bounded by the queue parallelism.
/// </para>
/// <para>
/// <strong>State lifecycle</strong>: budgets are process-local and lost on restart (fresh
/// buckets restart with full burst unless <see cref="RateLimitPolicy.StartEmpty"/> is set).
/// Idle keys (TAT in the past, no outstanding reservations — a state behaviorally identical to
/// a fresh bucket) are evicted opportunistically and by a periodic sweep. When
/// <see cref="RateLimiterOptions.MaxTrackedKeys"/> is exceeded, acquisitions for new keys fail
/// open (execute untracked) with a rate-limited warning.
/// </para>
/// </remarks>
public sealed class InMemoryKeyedRateLimiter : IKeyedRateLimiter, IDisposable
{
    private sealed class KeyState
    {
        public readonly object Gate = new();
        public long TatTicks;                              // 0 = unset (fresh key)
        public Dictionary<Guid, Reservation>? Reservations;
        public bool Dead;                                  // two-phase eviction flag (set under Gate)
    }

    private readonly record struct Reservation(long SlotTicks, long ExpiryTicks, long TatAfterTicks, long TTicks);

    private readonly ConcurrentDictionary<RateLimiterKey, KeyState> _keys = new();
    private readonly RateLimiterOptions _options;
    private readonly IEverTaskLogger<InMemoryKeyedRateLimiter> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _sweepTimer;

    private long _failOpenCount;
    private long _lastFailOpenWarningTicks;

    /// <summary>
    /// Extra slack added to a reservation's natural lifetime (slot + period) before it lapses,
    /// covering the scheduler check interval (1 s) and the full-queue retry delay (2 s).
    /// Internal for testing purposes.
    /// </summary>
    internal TimeSpan ReservationExpiryMargin { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Total acquisitions that failed open due to the tracked-keys cap. Internal: read by the gate for monitoring.</summary>
    internal long FailOpenCount => Interlocked.Read(ref _failOpenCount);

    /// <summary>Number of tracked (task type, key) buckets. Internal for testing/monitoring purposes.</summary>
    internal int TrackedKeyCount => _keys.Count;

    /// <summary>
    /// Creates the limiter.
    /// </summary>
    /// <param name="options">Global limiter knobs (key cardinality, key length).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">
    /// Clock abstraction for unit tests ONLY. Production must use the default
    /// (<see cref="TimeProvider.System"/>): slot math must share the schedulers' wall-clock
    /// UTC domain (<see cref="DateTimeOffset.UtcNow"/>), never a monotonic clock.
    /// </param>
    /// <param name="sweepInterval">Idle-key sweep interval. Defaults to 5 minutes.</param>
    public InMemoryKeyedRateLimiter(
        RateLimiterOptions options,
        IEverTaskLogger<InMemoryKeyedRateLimiter> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? sweepInterval = null)
    {
        _options      = options ?? throw new ArgumentNullException(nameof(options));
        _logger       = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var interval = sweepInterval ?? TimeSpan.FromMinutes(5);
        if (interval > TimeSpan.Zero)
        {
            _sweepTimer = _timeProvider.CreateTimer(
                static state => ((InMemoryKeyedRateLimiter)state!).SweepIdleKeys(),
                this, interval, interval);
        }
    }

    /// <inheritdoc />
    public ValueTask<RateLimitDecision> TryAcquireAsync(
        RateLimitPolicy policy, Type taskType, string key, Guid reservationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(taskType);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var limiterKey = new RateLimiterKey(taskType, NormalizeKey(key));
        var nowTicks   = _timeProvider.GetUtcNow().UtcTicks;

        while (true)
        {
            if (!_keys.TryGetValue(limiterKey, out var state))
            {
                // L4 key-cardinality bound: a new key beyond the cap fails OPEN (fresh untracked
                // bucket → execute) rather than failing the task or exhausting memory.
                if (_keys.Count >= _options.MaxTrackedKeys)
                {
                    RegisterFailOpen(taskType, nowTicks);
                    return new ValueTask<RateLimitDecision>(new RateLimitDecision(true, default));
                }

                state = _keys.GetOrAdd(limiterKey, static _ => new KeyState());
            }

            lock (state.Gate)
            {
                // Acquire-vs-eviction race: the state was evicted between lookup and lock.
                // Retry with a fresh state (the dead one is already unlinked or about to be).
                if (state.Dead)
                    continue;

                return new ValueTask<RateLimitDecision>(AcquireCore(state, policy, reservationId, nowTicks));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(Type taskType, string key, Guid reservationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_keys.TryGetValue(new RateLimiterKey(taskType, NormalizeKey(key)), out var state))
        {
            lock (state.Gate)
            {
                if (!state.Dead
                    && state.Reservations != null
                    && state.Reservations.Remove(reservationId, out var reservation))
                {
                    // Newest-only rollback: sound only when no later booking advanced the TAT.
                    // A general rollback would re-open capacity already promised to later
                    // reservations; non-newest orphans simply lapse via TTL (waste = one T).
                    if (state.TatTicks == reservation.TatAfterTicks)
                        state.TatTicks = reservation.TatAfterTicks - reservation.TTicks;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Core GCRA decision. Must be called under the key's lock.
    /// </summary>
    private RateLimitDecision AcquireCore(KeyState state, RateLimitPolicy policy, Guid reservationId, long nowTicks)
    {
        var tTicks = policy.Period.Ticks / policy.Permits;
        if (tTicks <= 0)
            tTicks = 1;
        var tauTicks = tTicks * (policy.Burst - 1);

        PurgeExpiredReservations(state, nowTicks);

        // 1) Idempotent redemption: this task already holds a reserved slot
        if (state.Reservations != null && state.Reservations.TryGetValue(reservationId, out var reservation))
        {
            if (nowTicks >= reservation.SlotTicks)
            {
                // Capacity was accounted at reserve time: redeem WITHOUT touching the TAT
                state.Reservations.Remove(reservationId);
                return new RateLimitDecision(true, default);
            }

            // Early redelivery (guard overlap): same slot, no extra capacity consumed
            return new RateLimitDecision(false, new DateTimeOffset(reservation.SlotTicks, TimeSpan.Zero));
        }

        // Fresh key: a default bucket starts full (burst available); StartEmpty starts at the
        // steady rate (no accumulated burst)
        if (state.TatTicks == 0)
            state.TatTicks = policy.StartEmpty ? nowTicks + tauTicks : nowTicks;

        // 2) Conforming: within burst tolerance of the theoretical arrival time
        if (nowTicks >= state.TatTicks - tauTicks)
        {
            state.TatTicks = Math.Max(nowTicks, state.TatTicks) + tTicks;
            return new RateLimitDecision(true, default);
        }

        // 3) Reserve the next slot
        var slotTicks = state.TatTicks - tauTicks;

        // L3 horizon: never book capacity for slots beyond the policy horizon — the gate maps
        // this unbooked deferral to a terminal rejection, and per-key state must stay bounded
        // (≈ horizon / T entries)
        if (slotTicks - nowTicks > policy.MaxReservationHorizon.Ticks)
            return new RateLimitDecision(false, new DateTimeOffset(slotTicks, TimeSpan.Zero));

        var tatAfter = state.TatTicks + tTicks;
        var expiry   = slotTicks + policy.Period.Ticks + ReservationExpiryMargin.Ticks;

        (state.Reservations ??= new Dictionary<Guid, Reservation>())
            .Add(reservationId, new Reservation(slotTicks, expiry, tatAfter, tTicks));
        state.TatTicks = tatAfter;

        return new RateLimitDecision(false, new DateTimeOffset(slotTicks, TimeSpan.Zero));
    }

    private static void PurgeExpiredReservations(KeyState state, long nowTicks)
    {
        if (state.Reservations is not { Count: > 0 })
            return;

        List<Guid>? expired = null;
        foreach (var entry in state.Reservations)
        {
            if (entry.Value.ExpiryTicks < nowTicks)
                (expired ??= []).Add(entry.Key);
        }

        if (expired == null)
            return;

        foreach (var id in expired)
            state.Reservations.Remove(id);
    }

    /// <summary>
    /// Evicts idle keys: TAT not in the future AND no outstanding reservations — by GCRA math a
    /// state behaviorally identical to a fresh bucket (behavior-preserving for default policies;
    /// for StartEmpty policies re-creation is at most stricter, never a rate violation).
    /// Runs on a periodic timer; internal so tests can trigger it deterministically.
    /// </summary>
    internal void SweepIdleKeys()
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;

        foreach (var kvp in _keys)
        {
            var state = kvp.Value;
            lock (state.Gate)
            {
                if (state.Dead)
                    continue;

                PurgeExpiredReservations(state, nowTicks);

                if (state.TatTicks != 0
                    && state.TatTicks <= nowTicks
                    && state.Reservations is not { Count: > 0 })
                {
                    // Two-phase eviction: mark dead under the lock, then remove by reference.
                    // A concurrent acquirer holding the stale state observes Dead and retries.
                    state.Dead = true;
                    ((ICollection<KeyValuePair<RateLimiterKey, KeyState>>)_keys).Remove(kvp);
                }
            }
        }
    }

    /// <summary>
    /// Number of outstanding reservations for a key. Internal for testing purposes.
    /// </summary>
    internal int GetReservationCount(Type taskType, string key)
    {
        if (!_keys.TryGetValue(new RateLimiterKey(taskType, NormalizeKey(key)), out var state))
            return 0;

        lock (state.Gate)
        {
            return state.Reservations?.Count ?? 0;
        }
    }

    private void RegisterFailOpen(Type taskType, long nowTicks)
    {
        Interlocked.Increment(ref _failOpenCount);

        // Rate-limited warning: at most one per 30 seconds, or the warning itself becomes a storm
        var last = Interlocked.Read(ref _lastFailOpenWarningTicks);
        if (nowTicks - last >= TimeSpan.FromSeconds(30).Ticks
            && Interlocked.CompareExchange(ref _lastFailOpenWarningTicks, nowTicks, last) == last)
        {
            _logger.LogWarning(
                "Rate limiter tracked-keys cap ({MaxTrackedKeys}) reached: new keys fail OPEN " +
                "(tasks execute without throttling). Task type: {TaskType}. Total fail-open count: {FailOpenCount}",
                _options.MaxTrackedKeys, taskType.Name, FailOpenCount);
        }
    }

    private string NormalizeKey(string key)
    {
        if (key.Length <= _options.MaxKeyLength)
            return key;

        // Longer keys are hashed: bounded memory, deterministic identity
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash);
    }

    public void Dispose() => _sweepTimer?.Dispose();
}

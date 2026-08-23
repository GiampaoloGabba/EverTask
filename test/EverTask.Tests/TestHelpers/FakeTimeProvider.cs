namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Deterministic manual clock for unit tests: time moves only via <see cref="Advance"/>, and so do the timers
/// created from it.
/// </summary>
/// <remarks>
/// Overriding <see cref="GetUtcNow"/> alone is not enough to test P9. Every wait the scheduling pipeline
/// performs — both schedulers, the parking lot — is a <c>Task.Delay(delay, timeProvider, ct)</c>, and
/// <see cref="TimeProvider"/>'s base <see cref="CreateTimer"/> hands out a REAL system timer: the delay would
/// still elapse in wall time, so a test that freezes the clock and then waits would be proving that real time
/// passes, not that the injected clock drives the wait. With the virtual timers below a frozen clock means a
/// wait that never ends, and <see cref="Advance"/> is the only thing that can end it.
/// </remarks>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object          _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private          DateTimeOffset  _utcNow;

    public FakeTimeProvider(DateTimeOffset? startUtc = null)
    {
        _utcNow = startUtc ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new FakeTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Timers currently waiting for the clock to reach them.</summary>
    public int PendingTimerCount
    {
        get { lock (_gate) return _timers.Count(t => t.IsArmed); }
    }

    /// <summary>
    /// Waits — in REAL time — until the code under test has armed <paramref name="count"/> timers on this
    /// clock. Advancing before that is the one race a virtual clock still has: the delay would be created
    /// after the jump and would then be measured from the new instant.
    /// </summary>
    public async Task<bool> WaitForPendingTimersAsync(int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (PendingTimerCount >= count)
                return true;

            await Task.Delay(10);
        }

        return PendingTimerCount >= count;
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "A fake clock only moves forward.");

        DateTimeOffset target;
        lock (_gate) target = _utcNow + delta;

        SetUtcNow(target);
    }

    /// <summary>
    /// Moves the clock to <paramref name="target"/>, firing every timer it passes IN ORDER and with the clock
    /// reading that timer's own due instant while its callback runs — a callback that arms another timer must
    /// measure it from when it actually ran, not from the end of the jump.
    /// </summary>
    public void SetUtcNow(DateTimeOffset target)
    {
        while (true)
        {
            FakeTimer? due;

            lock (_gate)
            {
                due = _timers.Where(t => t.IsArmed && t.DueAtUtc <= target)
                             .OrderBy(t => t.DueAtUtc)
                             .FirstOrDefault();

                if (due == null)
                {
                    if (target > _utcNow)
                        _utcNow = target;
                    return;
                }

                if (due.DueAtUtc > _utcNow)
                    _utcNow = due.DueAtUtc;
            }

            // Outside the lock: the callback may create, re-arm or dispose timers, and a Task.Delay
            // continuation resumed by it runs the awaiting code inline.
            due.Fire();
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool     _disposed;

        public DateTimeOffset DueAtUtc { get; private set; }
        public bool           IsArmed  { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var fireNow = false;

            lock (owner._gate)
            {
                if (_disposed)
                    return false;

                _period = period;

                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    IsArmed = false;
                }
                else if (dueTime <= TimeSpan.Zero)
                {
                    // A real timer armed for "now" fires without anyone moving the clock, and a wait that
                    // was already over must not hang until the next Advance.
                    IsArmed  = false;
                    DueAtUtc = owner._utcNow;
                    fireNow  = true;
                }
                else
                {
                    IsArmed  = true;
                    DueAtUtc = owner._utcNow + dueTime;
                }
            }

            if (fireNow)
                ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);

            return true;
        }

        internal void Fire()
        {
            lock (owner._gate)
            {
                if (_disposed || !IsArmed)
                    return;

                // Covers Timeout.InfiniteTimeSpan too, which is negative: a one-shot timer disarms itself.
                if (_period <= TimeSpan.Zero)
                    IsArmed = false;
                else
                    DueAtUtc = owner._utcNow + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                IsArmed   = false;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

namespace EverTask.Scheduler;

/// <summary>
/// A scheduler loop's sleep: a race between the wake-up signal a registration raises and the scheduling
/// clock's own delay (P9).
/// </summary>
/// <remarks>
/// Shared by <see cref="PeriodicTimerScheduler"/> and every shard of <see cref="ShardedScheduler"/>. It is a
/// race and not <c>SemaphoreSlim.WaitAsync(timeout)</c>, whose timeout is hard-wired to the real clock: the
/// loop would keep sleeping in wall time no matter which <see cref="TimeProvider"/> the rest of the pipeline
/// follows.
/// <para>
/// It also carries the invariant a second copy would eventually lose: the signal waiter is created ONCE and
/// KEPT across iterations when the delay wins the race. Abandoning it would let it silently consume the next
/// <c>Release</c> that nobody is watching for, and the scheduler would miss a wake-up.
/// </para>
/// </remarks>
internal sealed class SchedulerWakeUp(TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _pending;

    // The wake-up wait carried across loop iterations: see the remarks on WaitAsync.
    private Task? _pendingSignalWait;

    /// <summary>Wakes the loop, if it is asleep.</summary>
    /// <remarks>
    /// <c>Interlocked.CompareExchange</c> makes the check-then-act atomic on a semaphore of capacity one: two
    /// concurrent registrations would otherwise both <c>Release</c> and throw <c>SemaphoreFullException</c>.
    /// A disposal racing this is expected shutdown — the registration stays parked and the task is recovered
    /// at the next startup.
    /// </remarks>
    public void Signal()
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            return;

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with a Schedule call.
        }
    }

    /// <summary>Clears the pending flag, once the signal really has been consumed.</summary>
    public void Consumed() => Interlocked.Exchange(ref _pending, 0);

    /// <summary>
    /// Sleeps until either the wake-up signal arrives or <paramref name="waitTime"/> elapses on the
    /// scheduling clock (null waits for the signal only). Returns true when the signal won.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan? waitTime, CancellationToken cancellationToken)
    {
        _pendingSignalWait ??= _signal.WaitAsync(cancellationToken);

        if (waitTime == null)
        {
            await _pendingSignalWait.ConfigureAwait(false);
            _pendingSignalWait = null;
            return true;
        }

        using var delayCts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var       delayTask = Task.Delay(waitTime.Value, timeProvider, delayCts.Token);

        var winner = await Task.WhenAny(_pendingSignalWait, delayTask).ConfigureAwait(false);

        if (ReferenceEquals(winner, delayTask))
        {
            await delayTask.ConfigureAwait(false); // surfaces shutdown cancellation to the loop
            return false;
        }

        var signalWait = _pendingSignalWait;
        _pendingSignalWait = null;

        // Stop the losing timer and observe its cancellation, so neither a timer nor a faulted task lingers.
        await delayCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await delayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: we cancelled it ourselves after the signal won.
        }

        await signalWait.ConfigureAwait(false); // surfaces shutdown cancellation to the loop
        return true;
    }

    public void Dispose() => _signal.Dispose();
}

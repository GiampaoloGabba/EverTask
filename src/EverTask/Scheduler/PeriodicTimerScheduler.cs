using System.Collections.Concurrent;
using EverTask.Configuration;

namespace EverTask.Scheduler;

/// <summary>
/// High-performance scheduler implementation using SemaphoreSlim for wake-up signaling.
/// This scheduler reduces lock contention by 90%+ compared to TimerScheduler by eliminating
/// continuous UpdateTimer() calls and using dynamic delay calculation based on the next task.
/// </summary>
/// <remarks>
/// Performance characteristics:
/// - Zero CPU when queue empty (sleeps on semaphore)
/// - Reduced lock contention (no UpdateTimer() on every Schedule())
/// - Wake-up anticipato when new urgent tasks arrive
/// - Dynamic delay based on next task execution time
///
/// Dispatch characteristics:
/// - Non-blocking dispatch: a full worker queue never stalls the scheduler loop
///   (no head-of-line blocking across queues); the task is retried with a backoff.
/// - Idempotent scheduling per PersistenceId (latest wins): scheduling the same task twice
///   (e.g. startup recovery + taskKey re-registration) executes it once.
/// </remarks>
public class PeriodicTimerScheduler : IScheduler, IDisposable
{
    private readonly ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> _queue;
    private readonly ConcurrentDictionary<Guid, TaskHandlerExecutor> _scheduledItems;
    private readonly IWorkerQueueManager _queueManager;
    private readonly IEverTaskLogger<PeriodicTimerScheduler> _logger;
    private readonly TimeSpan _checkInterval;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _wakeUpSignal;
    private readonly TimeProvider _timeProvider;
    private int _wakeUpPending;
    private volatile bool _disposed;

    // The wake-up wait carried across loop iterations. See WaitForWakeUpAsync: when the delay wins the
    // race the same waiter is reused, so a Release issued meanwhile is never swallowed by an abandoned one.
    private Task? _pendingSignalWait;

    /// <summary>
    /// Delay before retrying the dispatch of a due task whose target queue is full.
    /// Internal for testing purposes.
    /// </summary>
    internal TimeSpan FullQueueRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

#if DEBUG
    // For tests purpose
    internal TimeSpan LastCalculatedDelay { get; private set; }
#endif

    /// <summary>
    /// The pre-P9 constructor, kept as a real overload so an assembly compiled against the previous release
    /// still binds (P6/X6); the scheduling clock arrives through the overload below, which the container
    /// picks because it is the longest one it can satisfy.
    /// </summary>
    public PeriodicTimerScheduler(
        IWorkerQueueManager queueManager,
        IEverTaskLogger<PeriodicTimerScheduler> logger,
        TimeSpan? checkInterval = null,
        ITaskStorage? taskStorage = null) // taskStorage kept for signature compatibility (no longer used)
        : this(queueManager, logger, checkInterval, taskStorage, null) { }

    public PeriodicTimerScheduler(
        IWorkerQueueManager queueManager,
        IEverTaskLogger<PeriodicTimerScheduler> logger,
        TimeSpan? checkInterval,
        ITaskStorage? taskStorage, // kept for signature compatibility (no longer used)
        TimeProvider? timeProvider)
    {
        _queueManager = queueManager;
        _logger = logger;
        _ = taskStorage;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queue = new ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset>();
        _scheduledItems = new ConcurrentDictionary<Guid, TaskHandlerExecutor>();
        _cts = new CancellationTokenSource();
        // Captured before any dispatch: accessing _cts.Token after Dispose would throw
        _shutdownToken = _cts.Token;
        _wakeUpSignal = new SemaphoreSlim(0, 1);

        // Default: check ogni 1 secondo (bilanciamento ottimale)
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(1);

        // Avvia background loop
        _ = ProcessScheduledTasksAsync(_shutdownToken);
    }

    internal ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> GetQueue() => _queue;

    public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) =>
        Register(item, nextRecurringRun, refuseSuperseded: false);

    /// <inheritdoc />
    public bool TrySchedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) =>
        Register(item, nextRecurringRun, refuseSuperseded: true);

    /// <param name="refuseSuperseded">
    /// True to leave a registration carrying a newer schedule version in place and answer false, instead of
    /// replacing it latest-wins.
    /// </param>
    private bool Register(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun, bool refuseSuperseded)
    {
        DateTimeOffset scheduledTime;
        if (item.RecurringTask != null && nextRecurringRun != null)
        {
            _logger.SchedulingTask(item.PersistenceId, nextRecurringRun.Value);
            scheduledTime = nextRecurringRun.Value;
        }
        else
        {
            ArgumentNullException.ThrowIfNull(item.ExecutionTime);
            scheduledTime = item.ExecutionTime.Value;
        }

        // Post-dispose guard: scheduling after shutdown must not throw into the caller.
        // The task stays in its recoverable status and is re-dispatched at the next startup.
        if (_disposed)
        {
            _logger.SchedulerDisposed(item.PersistenceId);
            return false;
        }

        // Latest-wins registration per PersistenceId: a previously parked entry for the same task
        // (e.g. recovery racing with a taskKey re-registration at startup) becomes stale and is
        // discarded at dequeue time, so the task executes only once per occurrence.
        // CU19: also evict the stale node from the heap now, so repeated far-future re-registrations
        // of the same id do not accumulate orphans retained until their due time.
        // The swap itself is atomic so that refuseSuperseded can DECIDE on what it replaces: comparing
        // outside it would leave a window in which the newer registration arrives between the comparison
        // and the write, which is the whole race the conditional registration exists to close.
        if (!SwapRegistration(item, refuseSuperseded))
            return false;

        _queue.Enqueue(item, scheduledTime);

        // Sveglia il timer se è dormiente (coda era vuota)
        // Thread-safe: usa Interlocked.CompareExchange per garantire che solo un thread
        // possa segnalare il semaforo, eliminando la race condition check-then-act.
        // Il flag _wakeUpPending viene resettato dopo che WaitAsync consuma il segnale.
        if (Interlocked.CompareExchange(ref _wakeUpPending, 1, 0) == 0)
        {
            try
            {
                _wakeUpSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently with this Schedule: the registration stays parked and
                // the task is recovered at the next startup (same as the guard above)
            }
        }

        return true;
    }

    /// <summary>
    /// Puts <paramref name="item"/> in the registry, evicting whatever it replaces from the heap.
    /// </summary>
    /// <returns>False only when a newer registration was found and <paramref name="refuseSuperseded"/> asked
    /// for it to be preserved.</returns>
    private bool SwapRegistration(TaskHandlerExecutor item, bool refuseSuperseded)
    {
        while (true)
        {
            if (!_scheduledItems.TryGetValue(item.PersistenceId, out var previous))
            {
                if (_scheduledItems.TryAdd(item.PersistenceId, item))
                    return true;

                continue;
            }

            if (ReferenceEquals(previous, item))
                return true;

            if (refuseSuperseded && previous.ScheduleVersion > item.ScheduleVersion)
            {
                _logger.SupersededRegistrationKept(item.PersistenceId, item.ScheduleVersion,
                    previous.ScheduleVersion);

                return false;
            }

            if (!_scheduledItems.TryUpdate(item.PersistenceId, item, previous))
                continue;

            _queue.Remove(previous);
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryUnschedule(Guid persistenceId)
    {
        // The orphan entry left in the priority queue is discarded by the staleness
        // check in ProcessReadyTasksAsync.
        return _scheduledItems.TryRemove(persistenceId, out _);
    }

    /// <inheritdoc />
    public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected)
    {
        // Conditional remove (same pattern as the consume in ProcessReadyTasksAsync):
        // a concurrent newer registration for the same task is preserved.
        return _scheduledItems.TryRemove(new KeyValuePair<Guid, TaskHandlerExecutor>(persistenceId, expected));
    }

    /// <inheritdoc />
    public bool IsScheduled(Guid persistenceId) => _scheduledItems.ContainsKey(persistenceId);

    /// <inheritdoc />
    public bool SupportsScheduleInspection => true;

    private async Task ProcessScheduledTasksAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Calcola delay dinamico basato sul prossimo task
                var delay = CalculateNextDelay();

                if (delay == Timeout.InfiniteTimeSpan)
                {
                    // Coda vuota: dormi fino a quando Schedule() chiama Release()
                    _logger.QueueEmpty();
                    await WaitForWakeUpAsync(null, cancellationToken).ConfigureAwait(false);

                    // Resetta il flag di wake-up dopo aver consumato il segnale
                    Interlocked.Exchange(ref _wakeUpPending, 0);
                }
                else
                {
                    // Attendi il minore tra: delay calcolato o checkInterval
                    var waitTime = delay < _checkInterval ? delay : _checkInterval;

                    var signaled = await WaitForWakeUpAsync(waitTime, cancellationToken).ConfigureAwait(false);

                    // Resetta il flag solo se il semaforo è stato effettivamente segnalato
                    if (signaled)
                    {
                        Interlocked.Exchange(ref _wakeUpPending, 0);
                    }
                }

                // Processa task pronti
                await ProcessReadyTasksAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // Shutdown
            }
            catch (ObjectDisposedException)
            {
                // Dispose() cancelled the loop and disposed the wake-up semaphore: a WaitAsync racing
                // that disposal is expected shutdown, not an error (F12). Treat it like cancellation.
                break;
            }
            catch (Exception ex)
            {
                _logger.ErrorProcessingScheduledTasks(ex);
            }
        }
    }

    /// <summary>
    /// Sleeps until either the wake-up signal arrives or <paramref name="waitTime"/> elapses on the
    /// scheduling clock (null waits for the signal only). Returns true when the signal won.
    /// </summary>
    /// <remarks>
    /// A race, not <c>SemaphoreSlim.WaitAsync(timeout)</c>: that timeout is hard-wired to the real clock, so
    /// the loop would keep sleeping in wall time no matter which <see cref="TimeProvider"/> the rest of the
    /// pipeline follows. The signal waiter is created once and KEPT across iterations when the delay wins —
    /// abandoning it would let it silently consume the next <c>Release</c> that nobody is watching for, and
    /// the scheduler would miss a wake-up.
    /// </remarks>
    private async Task<bool> WaitForWakeUpAsync(TimeSpan? waitTime, CancellationToken cancellationToken)
    {
        _pendingSignalWait ??= _wakeUpSignal.WaitAsync(cancellationToken);

        if (waitTime == null)
        {
            await _pendingSignalWait.ConfigureAwait(false);
            _pendingSignalWait = null;
            return true;
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var       delayTask = Task.Delay(waitTime.Value, _timeProvider, delayCts.Token);

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

    private TimeSpan CalculateNextDelay()
    {
        if (_queue.TryPeek(out _, out var nextScheduledTime))
        {
            var delay = nextScheduledTime - _timeProvider.GetUtcNow();

            // Se delay negativo, esegui subito
            if (delay < TimeSpan.Zero)
            {
#if DEBUG
                LastCalculatedDelay = TimeSpan.Zero;
#endif
                return TimeSpan.Zero;
            }

            // Limita delay massimo (come TimerScheduler originale)
            // Previene problemi con delay molto lunghi
            if (delay > TimeSpan.FromHours(2))
            {
#if DEBUG
                LastCalculatedDelay = TimeSpan.FromHours(1.5);
#endif
                return TimeSpan.FromHours(1.5);
            }

#if DEBUG
            LastCalculatedDelay = delay;
#endif
            return delay;
        }

        // Coda vuota
#if DEBUG
        LastCalculatedDelay = Timeout.InfiniteTimeSpan;
#endif
        return Timeout.InfiniteTimeSpan;
    }

    private async Task ProcessReadyTasksAsync()
    {
        var now = _timeProvider.GetUtcNow();

        // Dequeue tutti i task pronti
        while (_queue.TryPeek(out var item, out var scheduledTime) && scheduledTime <= now)
        {
            if (!_queue.TryDequeue(out item, out _))
                continue;

            // Stale entry: the task was re-scheduled with a newer registration (latest wins)
            // or already dispatched. Drop this occurrence silently.
            if (!_scheduledItems.TryGetValue(item.PersistenceId, out var current) || !ReferenceEquals(current, item))
                continue;

            var result = await DispatchToWorkerQueue(item).ConfigureAwait(false);

            if (result is EnqueueResult.QueueFull or EnqueueResult.DuplicateInProcess)
            {
                // QueueFull: target queue saturated. DuplicateInProcess: our slot fired while the
                // previous delivery of the same task was still unwinding (its registration not
                // yet released). Either way: park the task and retry later WITHOUT blocking the
                // loop, so tasks targeting other queues keep flowing (no head-of-line blocking).
                _logger.TaskNotEnqueued(item.PersistenceId, result, FullQueueRetryDelay);
                _queue.Enqueue(item, _timeProvider.GetUtcNow() + FullQueueRetryDelay);
            }
            else
            {
                // Enqueued, discarded or failed: this registration is consumed.
                // Conditional remove: keep a concurrent newer registration alive.
                _scheduledItems.TryRemove(new KeyValuePair<Guid, TaskHandlerExecutor>(item.PersistenceId, item));
            }
        }
    }

    internal async Task<EnqueueResult> DispatchToWorkerQueue(TaskHandlerExecutor item)
    {
        try
        {
            var queueName = item.QueueName ??
                (item.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

            _logger.DispatchingTask(item.PersistenceId, queueName);

            // Non-blocking: a full queue must not stall the scheduler loop
            return await _queueManager.TryEnqueueImmediate(queueName, item, _shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Scheduler shutdown while dispatching: leave the task in its recoverable status,
            // startup recovery will re-dispatch it. Marking it Failed would lose it permanently.
            _logger.DispatchCancelled(item.PersistenceId);
            return EnqueueResult.Discarded;
        }
        catch (Exception ex)
        {
            // Transient failure (typically storage): park and retry with backoff instead of
            // marking Failed, which would make a one-shot task permanently unrecoverable.
            _logger.UnableToDispatchTask(ex, item.PersistenceId, FullQueueRetryDelay);
            return EnqueueResult.QueueFull;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _wakeUpSignal.Dispose();
    }
}

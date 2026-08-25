using System.Collections.Concurrent;
using EverTask.Configuration;

namespace EverTask.Scheduler;

/// <summary>
/// High-performance scheduler using multiple independent timer shards.
/// Recommended for workloads exceeding 10k Schedule() calls/sec or 100k+ scheduled tasks.
/// </summary>
/// <remarks>
/// This scheduler divides the workload across multiple independent shards (each with its own timer and priority queue)
/// to reduce lock contention and improve throughput. Each shard operates independently, providing:
/// - Reduced lock contention (divided by shard count)
/// - Better spike handling (independent processing)
/// - Complete failure isolation (issues in one shard don't affect others)
///
/// Trade-offs:
/// - Additional memory overhead (~300 bytes per shard)
/// - Additional background threads (1 per shard)
/// - Slightly more complex debugging (multiple timers)
///
/// Recommended shard count: 4-16 for most workloads
/// Auto-scaling default: Environment.ProcessorCount
///
/// Dispatch characteristics (same as <see cref="PeriodicTimerScheduler"/>):
/// - Non-blocking dispatch: a full worker queue never stalls a shard loop
///   (no head-of-line blocking across queues); the task is retried with a backoff.
/// - Idempotent scheduling per PersistenceId (latest wins): the same task scheduled twice
///   executes once. Sharding is hash-based on PersistenceId, so duplicate registrations
///   always land on the same shard.
/// </remarks>
public class ShardedScheduler : IScheduler, IDisposable
{
    /// <summary>
    /// Represents a single scheduler shard with its own timer and priority queue.
    /// </summary>
    private sealed class Shard : IDisposable
    {
        private readonly ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> _queue;
        private readonly ConcurrentDictionary<Guid, TaskHandlerExecutor> _scheduledItems;
        private readonly SemaphoreSlim _wakeUpSignal;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _shutdownToken;
        private readonly IWorkerQueueManager _queueManager;
        private readonly IEverTaskLogger<ShardedScheduler> _logger;
        private readonly ShardedScheduler _owner;
        private readonly TimeProvider _timeProvider;
        private readonly int _shardId;
        private int _wakeUpPending;
        private volatile bool _disposed;

        // Kept across loop iterations when the delay wins the race: see WaitForWakeUpAsync.
        private Task? _pendingSignalWait;

        public Shard(
            int shardId,
            ShardedScheduler owner,
            IWorkerQueueManager queueManager,
            IEverTaskLogger<ShardedScheduler> logger,
            TimeProvider timeProvider)
        {
            _shardId        = shardId;
            _owner          = owner;
            _timeProvider   = timeProvider;
            _queue          = new();
            _scheduledItems = new();
            _wakeUpSignal   = new(0, 1);
            _cts            = new();
            _queueManager   = queueManager;
            _logger         = logger;

            // Captured before any dispatch: accessing _cts.Token after Dispose would throw
            _shutdownToken = _cts.Token;

            // Avvia background loop per questo shard
            _ = ProcessScheduledTasksAsync(_shutdownToken);
        }

        /// <summary>
        /// Schedules a task for execution in this shard.
        /// </summary>
        /// <param name="refuseSuperseded">
        /// True to leave a registration carrying a newer schedule version in place and answer false, instead
        /// of replacing it latest-wins.
        /// </param>
        public bool Schedule(TaskHandlerExecutor item, DateTimeOffset scheduledTime, bool refuseSuperseded)
        {
            // Post-dispose guard: scheduling after shutdown must not throw into the caller.
            // The task stays in its recoverable status and is re-dispatched at the next startup.
            if (_disposed)
            {
                _logger.ShardSchedulerDisposed(_shardId, item.PersistenceId);
                return false;
            }

            _logger.ShardSchedulingTask(_shardId, item.PersistenceId, scheduledTime);

            // Latest-wins registration per PersistenceId: a previously parked entry for the same
            // task becomes stale and is discarded at dequeue time (single execution per occurrence).
            // CU19: also evict the stale node from the heap now, so repeated far-future
            // re-registrations of the same id do not accumulate orphans (symmetric with
            // PeriodicTimerScheduler). The swap is atomic so that refuseSuperseded can decide on what it
            // replaces: comparing outside it leaves the very window the conditional registration closes.
            if (!SwapRegistration(item, refuseSuperseded))
                return false;

            _queue.Enqueue(item, scheduledTime);

            // Sveglia il timer se è dormiente.
            // Interlocked.CompareExchange evita la race check-then-act sul semaforo (max count 1):
            // due Schedule() concorrenti con CurrentCount==0 lancerebbero SemaphoreFullException.
            if (Interlocked.CompareExchange(ref _wakeUpPending, 1, 0) == 0)
            {
                try
                {
                    _wakeUpSignal.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed concurrently with this Schedule: the registration stays parked
                    // and the task is recovered at the next startup (same as the guard above)
                }
            }

            return true;
        }

        /// <summary>
        /// Puts <paramref name="item"/> in this shard's registry, evicting whatever it replaces from the heap.
        /// </summary>
        /// <returns>False only when a newer registration was found and <paramref name="refuseSuperseded"/>
        /// asked for it to be preserved.</returns>
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

        /// <summary>
        /// Invalidates a parked registration in this shard, if present.
        /// </summary>
        public bool TryUnschedule(Guid persistenceId)
        {
            // The orphan entry left in the priority queue is discarded by the staleness check
            return _scheduledItems.TryRemove(persistenceId, out _);
        }

        /// <summary>
        /// Conditionally invalidates a parked registration in this shard: removed only if it is
        /// still the expected one (a concurrent newer registration is preserved).
        /// </summary>
        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected)
        {
            return _scheduledItems.TryRemove(new KeyValuePair<Guid, TaskHandlerExecutor>(persistenceId, expected));
        }

        /// <summary>
        /// True when any registration is parked in this shard for the given task.
        /// </summary>
        public bool IsScheduled(Guid persistenceId) => _scheduledItems.ContainsKey(persistenceId);

        /// <summary>Test seam (CU19): number of entries currently in this shard's priority queue.</summary>
        internal int QueueCount => _queue.Count;

        /// <summary>
        /// Background loop that processes scheduled tasks for this shard.
        /// </summary>
        private async Task ProcessScheduledTasksAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var delay = CalculateNextDelay();

                    if (delay == Timeout.InfiniteTimeSpan)
                    {
                        _logger.ShardQueueEmpty(_shardId);
                        await WaitForWakeUpAsync(null, ct).ConfigureAwait(false);
                        Interlocked.Exchange(ref _wakeUpPending, 0);
                    }
                    else
                    {
                        var signaled = await WaitForWakeUpAsync(delay, ct).ConfigureAwait(false);
                        if (signaled)
                        {
                            Interlocked.Exchange(ref _wakeUpPending, 0);
                        }
                    }

                    await ProcessReadyTasks().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Dispose() cancelled the loop and disposed the wake-up semaphore: a WaitAsync racing
                    // that disposal is expected shutdown, not an error (F12). Treat it like cancellation.
                    break;
                }
                catch (Exception ex)
                {
                    _logger.ShardErrorProcessingScheduledTasks(ex, _shardId);
                }
            }
        }

        /// <summary>
        /// Sleeps until either the wake-up signal arrives or <paramref name="waitTime"/> elapses on the
        /// scheduling clock (null waits for the signal only). Returns true when the signal won. Mirrors
        /// <c>PeriodicTimerScheduler.WaitForWakeUpAsync</c> — see it for why this is a race instead of
        /// <c>SemaphoreSlim.WaitAsync(timeout)</c> and why the signal waiter survives a lost race.
        /// </summary>
        private async Task<bool> WaitForWakeUpAsync(TimeSpan? waitTime, CancellationToken ct)
        {
            _pendingSignalWait ??= _wakeUpSignal.WaitAsync(ct);

            if (waitTime == null)
            {
                await _pendingSignalWait.ConfigureAwait(false);
                _pendingSignalWait = null;
                return true;
            }

            using var delayCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var       delayTask = Task.Delay(waitTime.Value, _timeProvider, delayCts.Token);

            var winner = await Task.WhenAny(_pendingSignalWait, delayTask).ConfigureAwait(false);

            if (ReferenceEquals(winner, delayTask))
            {
                await delayTask.ConfigureAwait(false); // surfaces shutdown cancellation to the loop
                return false;
            }

            var signalWait = _pendingSignalWait;
            _pendingSignalWait = null;

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

        /// <summary>
        /// Calculates the delay until the next task needs to be processed.
        /// </summary>
        private TimeSpan CalculateNextDelay()
        {
            if (_queue.TryPeek(out _, out var nextScheduledTime))
            {
                var delay = nextScheduledTime - _timeProvider.GetUtcNow();

                if (delay < TimeSpan.Zero)
                    return TimeSpan.Zero;

                // Limita delay massimo (come PeriodicTimerScheduler)
                if (delay > TimeSpan.FromHours(2))
                    return TimeSpan.FromHours(1.5);

                return delay;
            }

            return Timeout.InfiniteTimeSpan;
        }

        /// <summary>
        /// Processes all tasks that are ready for execution (scheduled time has passed).
        /// </summary>
        private async Task ProcessReadyTasks()
        {
            var now = _timeProvider.GetUtcNow();

            while (_queue.TryPeek(out var item, out var scheduledTime) && scheduledTime <= now)
            {
                if (!_queue.TryDequeue(out item, out _))
                    continue;

                // Stale entry (replaced by a newer registration or already dispatched): drop it
                if (!_scheduledItems.TryGetValue(item.PersistenceId, out var current) || !ReferenceEquals(current, item))
                    continue;

                var result = await DispatchToWorkerQueue(item).ConfigureAwait(false);

                if (result is EnqueueResult.QueueFull or EnqueueResult.DuplicateInProcess)
                {
                    // QueueFull: target queue saturated. DuplicateInProcess: slot fired while the
                    // previous delivery of the same task was still unwinding. Retry later without
                    // stalling this shard.
                    _logger.ShardTaskNotEnqueued(_shardId, item.PersistenceId, result, _owner.FullQueueRetryDelay);
                    _queue.Enqueue(item, _timeProvider.GetUtcNow() + _owner.FullQueueRetryDelay);
                }
                else
                {
                    // Conditional remove: keep a concurrent newer registration alive
                    _scheduledItems.TryRemove(new KeyValuePair<Guid, TaskHandlerExecutor>(item.PersistenceId, item));
                }
            }
        }

        /// <summary>
        /// Dispatches a task to the worker queue for execution.
        /// </summary>
        private async Task<EnqueueResult> DispatchToWorkerQueue(TaskHandlerExecutor item)
        {
            try
            {
                var queueName = item.QueueName ??
                                   (item.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

                _logger.ShardDispatchingTask(_shardId, item.PersistenceId, queueName);

                // Non-blocking: a full queue must not stall this shard's loop
                return await _queueManager.TryEnqueueImmediate(queueName, item, _shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shard shutdown while dispatching: leave the task in its recoverable status,
                // startup recovery will re-dispatch it. Marking it Failed would lose it permanently.
                _logger.ShardDispatchCancelled(_shardId, item.PersistenceId);
                return EnqueueResult.Discarded;
            }
            catch (Exception ex)
            {
                // Transient failure (typically storage): park and retry with backoff instead of
                // marking Failed, which would make a one-shot task permanently unrecoverable.
                _logger.ShardUnableToDispatchTask(ex, _shardId, item.PersistenceId, _owner.FullQueueRetryDelay);
                return EnqueueResult.QueueFull;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }

            _cts.Dispose();
            _wakeUpSignal.Dispose();
        }
    }

    private readonly Shard[] _shards;
    private readonly int _shardCount;
    private readonly IEverTaskLogger<ShardedScheduler> _logger;

    /// <summary>
    /// Delay before retrying the dispatch of a due task whose target queue is full.
    /// Internal for testing purposes.
    /// </summary>
    internal TimeSpan FullQueueRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Initializes a new instance of the <see cref="ShardedScheduler"/> class.
    /// </summary>
    /// <param name="queueManager">Worker queue manager for task dispatching.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="taskStorage">Optional task storage for persisting task states.</param>
    /// <param name="shardCount">Number of independent shards. 0 = auto-scale to ProcessorCount (minimum 4).</param>
    /// <remarks>
    /// The pre-P9 arity, kept as a real overload so an assembly compiled against the previous release still
    /// binds (P6/X6); the scheduling clock arrives through the overload below.
    /// </remarks>
    public ShardedScheduler(
        IWorkerQueueManager queueManager,
        IEverTaskLogger<ShardedScheduler> logger,
        ITaskStorage? taskStorage = null,
        int shardCount = 0)
        : this(queueManager, logger, taskStorage, shardCount, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShardedScheduler"/> class on an explicit scheduling clock.
    /// </summary>
    /// <param name="queueManager">Worker queue manager for task dispatching.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="taskStorage">Optional task storage for persisting task states.</param>
    /// <param name="shardCount">Number of independent shards. 0 = auto-scale to ProcessorCount (minimum 4).</param>
    /// <param name="timeProvider">The scheduling clock (P9). Null falls back to the real clock.</param>
    public ShardedScheduler(
        IWorkerQueueManager queueManager,
        IEverTaskLogger<ShardedScheduler> logger,
        ITaskStorage? taskStorage, // kept for signature compatibility (no longer used)
        int shardCount,
        TimeProvider? timeProvider)
    {
        _ = taskStorage;
        _logger     = logger;
        _shardCount = shardCount > 0 ? shardCount : Math.Max(4, Environment.ProcessorCount);

        _logger.InitializingShardedScheduler(_shardCount);

        var clock = timeProvider ?? TimeProvider.System;

        _shards = Enumerable.Range(0, _shardCount)
                            .Select(i => new Shard(i, this, queueManager, logger, clock))
                            .ToArray();
    }

    /// <summary>
    /// Schedules a task for execution using hash-based shard distribution.
    /// </summary>
    /// <param name="item">Task handler executor to schedule.</param>
    /// <param name="nextRecurringRun">Next execution time for recurring tasks (overrides item.ExecutionTime).</param>
    public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) =>
        Register(item, nextRecurringRun, refuseSuperseded: false);

    /// <inheritdoc />
    public bool TrySchedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) =>
        Register(item, nextRecurringRun, refuseSuperseded: true);

    private bool Register(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun, bool refuseSuperseded)
    {
        var scheduledTime = nextRecurringRun ?? item.ExecutionTime;
        ArgumentNullException.ThrowIfNull(scheduledTime);

        return GetShard(item.PersistenceId).Schedule(item, scheduledTime.Value, refuseSuperseded);
    }

    /// <inheritdoc />
    public bool TryUnschedule(Guid persistenceId)
    {
        // Hash-based sharding is deterministic: the registration, if any, lives in this shard
        return GetShard(persistenceId).TryUnschedule(persistenceId);
    }

    /// <inheritdoc />
    public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected)
    {
        // Hash-based sharding is deterministic: the registration, if any, lives in this shard
        return GetShard(persistenceId).TryUnschedule(persistenceId, expected);
    }

    /// <inheritdoc />
    public bool IsScheduled(Guid persistenceId) => GetShard(persistenceId).IsScheduled(persistenceId);

    /// <inheritdoc />
    public bool SupportsScheduleInspection => true;

    /// <summary>Test seam (CU19): entries in the priority queue of the shard owning this task id.</summary>
    internal int GetQueueCount(Guid persistenceId) => GetShard(persistenceId).QueueCount;

    private Shard GetShard(Guid persistenceId)
    {
        // Hash-based sharding per distribuzione uniforme
        // Use unsigned hash to prevent negative modulo when GetHashCode() returns int.MinValue
        var shardIndex = (int)((uint)persistenceId.GetHashCode() % (uint)_shardCount);
        return _shards[shardIndex];
    }

    /// <summary>
    /// Disposes all shards and releases resources.
    /// </summary>
    public void Dispose()
    {
        foreach (var shard in _shards)
        {
            shard.Dispose();
        }
    }
}

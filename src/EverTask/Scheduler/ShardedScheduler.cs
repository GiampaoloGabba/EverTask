using System.Collections.Concurrent;
using EverTask.Configuration;

namespace EverTask.Scheduler;

/// <summary>
/// High-performance scheduler using multiple independent timer shards.
/// Recommended for workloads exceeding 10k Schedule() calls/sec or 100k+ scheduled tasks.
/// </summary>
/// <remarks>
/// Each shard owns a timer and a priority queue, and costs one background loop plus a few hundred bytes.
/// Dispatch behaves as in <see cref="PeriodicTimerScheduler"/>: a full worker queue never stalls a shard loop
/// (the task is retried with a backoff), and scheduling is idempotent per PersistenceId (latest wins) —
/// sharding is hash-based on that id, so duplicate registrations always land on the same shard.
/// </remarks>
public class ShardedScheduler : IScheduler, IDisposable
{
    private sealed class Shard : IDisposable
    {
        private readonly ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> _queue;
        private readonly ScheduledRegistrations _registrations;
        private readonly SchedulerWakeUp _wakeUp;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _shutdownToken;
        private readonly IWorkerQueueManager _queueManager;
        private readonly IEverTaskLogger<ShardedScheduler> _logger;
        private readonly ShardedScheduler _owner;
        private readonly TimeProvider _timeProvider;
        private readonly int _shardId;
        private volatile bool _disposed;

        public Shard(
            int shardId,
            ShardedScheduler owner,
            IWorkerQueueManager queueManager,
            IEverTaskLogger<ShardedScheduler> logger,
            TimeProvider timeProvider)
        {
            _shardId       = shardId;
            _owner         = owner;
            _timeProvider  = timeProvider;
            _queue         = new();
            _cts           = new();
            _queueManager  = queueManager;
            _logger        = logger;
            _registrations = new ScheduledRegistrations(_queue, logger.SupersededRegistrationKept);
            _wakeUp        = new SchedulerWakeUp(timeProvider);

            // Captured before any dispatch: accessing _cts.Token after Dispose would throw
            _shutdownToken = _cts.Token;

            _ = ProcessScheduledTasksAsync(_shutdownToken);
        }

        /// <summary>Schedules a task for execution in this shard.</summary>
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

            // Latest-wins registration per PersistenceId: a previously parked entry for the same task is
            // evicted, so an occurrence executes once.
            if (!_registrations.Swap(item, refuseSuperseded))
                return false;

            _queue.Enqueue(item, scheduledTime);

            _wakeUp.Signal();

            return true;
        }

        /// <summary>Invalidates a parked registration in this shard, if present.</summary>
        public bool TryUnschedule(Guid persistenceId)
        {
            if (!_registrations.Remove(persistenceId))
                return false;

            _wakeUp.Signal();
            return true;
        }

        /// <summary>
        /// Conditionally invalidates a parked registration in this shard: removed only if it is
        /// still the expected one (a concurrent newer registration is preserved).
        /// </summary>
        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected)
        {
            if (!_registrations.Remove(persistenceId, expected))
                return false;

            _wakeUp.Signal();
            return true;
        }

        /// <summary>
        /// True when any registration is parked in this shard for the given task.
        /// </summary>
        public bool IsScheduled(Guid persistenceId) => _registrations.Contains(persistenceId);

        /// <summary>Test seam: number of entries currently in this shard's priority queue.</summary>
        internal int QueueCount => _queue.Count;

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
                        await _wakeUp.WaitAsync(null, ct).ConfigureAwait(false);
                        _wakeUp.Consumed();
                    }
                    else
                    {
                        var signaled = await _wakeUp.WaitAsync(delay, ct).ConfigureAwait(false);
                        if (signaled)
                        {
                            _wakeUp.Consumed();
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
                    // that disposal is expected shutdown, not an error.
                    break;
                }
                catch (Exception ex)
                {
                    _logger.ShardErrorProcessingScheduledTasks(ex, _shardId);
                }
            }
        }

        private TimeSpan CalculateNextDelay()
        {
            if (_queue.TryPeek(out _, out var nextScheduledTime))
            {
                var delay = nextScheduledTime - _timeProvider.GetUtcNow();

                if (delay < TimeSpan.Zero)
                    return TimeSpan.Zero;

                if (delay > TimeSpan.FromHours(2))
                    return TimeSpan.FromHours(1.5);

                return delay;
            }

            return Timeout.InfiniteTimeSpan;
        }

        private async Task ProcessReadyTasks()
        {
            var now = _timeProvider.GetUtcNow();

            while (_queue.TryPeek(out var item, out var scheduledTime) && scheduledTime <= now)
            {
                if (!_queue.TryDequeue(out item, out _))
                    continue;

                // Stale entry (replaced by a newer registration or already dispatched): drop it
                if (!_registrations.IsCurrent(item))
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
                    _registrations.Remove(item.PersistenceId, item);
                }
            }
        }

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
                // Already disposed
            }

            _cts.Dispose();
            _wakeUp.Dispose();
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
    /// Kept as a real overload so an assembly compiled against the previous release still binds; the
    /// scheduling clock arrives through the overload below.
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
    /// <param name="timeProvider">The scheduling clock. Null falls back to the real clock.</param>
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

    /// <summary>Test seam: entries in the priority queue of the shard owning this task id.</summary>
    internal int GetQueueCount(Guid persistenceId) => GetShard(persistenceId).QueueCount;

    private Shard GetShard(Guid persistenceId)
    {
        // Unsigned hash: a signed modulo goes negative when GetHashCode() returns int.MinValue
        var shardIndex = (int)((uint)persistenceId.GetHashCode() % (uint)_shardCount);
        return _shards[shardIndex];
    }

    /// <summary>Disposes all shards and releases resources.</summary>
    public void Dispose()
    {
        foreach (var shard in _shards)
        {
            shard.Dispose();
        }
    }
}

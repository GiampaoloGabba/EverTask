using EverTask.Configuration;
using EverTask.Logger;

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
/// </remarks>
public class ShardedScheduler : IScheduler, IDisposable
{
    /// <summary>
    /// Represents a single scheduler shard with its own timer and priority queue.
    /// </summary>
    private sealed class Shard : IDisposable
    {
        private readonly ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> _queue;
        private readonly SemaphoreSlim _wakeUpSignal;
        private readonly CancellationTokenSource _cts;
        private readonly IWorkerQueueManager _queueManager;
        private readonly ITaskStorage? _taskStorage;
        private readonly IEverTaskLogger<ShardedScheduler> _logger;
        private readonly int _shardId;
        private bool _disposed;

        public Shard(
            int shardId,
            IWorkerQueueManager queueManager,
            IEverTaskLogger<ShardedScheduler> logger,
            ITaskStorage? taskStorage)
        {
            _shardId = shardId;
            _queue = new();
            _wakeUpSignal = new(0, 1);
            _cts = new();
            _queueManager = queueManager;
            _logger = logger;
            _taskStorage = taskStorage;

            // Avvia background loop per questo shard
            _ = ProcessScheduledTasksAsync(_cts.Token);
        }

        /// <summary>
        /// Schedules a task for execution in this shard.
        /// </summary>
        public void Schedule(TaskHandlerExecutor item, DateTimeOffset scheduledTime)
        {
            _logger.LogDebug("Shard {ShardId}: Scheduling task {TaskId} for {ScheduledTime}",
                _shardId, item.PersistenceId, scheduledTime);

            _queue.Enqueue(item, scheduledTime);

            // Sveglia il timer se è dormiente
            if (_wakeUpSignal.CurrentCount == 0)
                _wakeUpSignal.Release();
        }

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
                        _logger.LogDebug("Shard {ShardId}: Queue empty, sleeping", _shardId);
                        await _wakeUpSignal.WaitAsync(ct);
                    }
                    else
                    {
                        await _wakeUpSignal.WaitAsync(delay, ct);
                    }

                    await ProcessReadyTasks();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Shard {ShardId}: Error processing scheduled tasks", _shardId);
                }
            }
        }

        /// <summary>
        /// Calculates the delay until the next task needs to be processed.
        /// </summary>
        private TimeSpan CalculateNextDelay()
        {
            if (_queue.TryPeek(out _, out var nextScheduledTime))
            {
                var delay = nextScheduledTime - DateTimeOffset.UtcNow;

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
            var now = DateTimeOffset.UtcNow;

            while (_queue.TryPeek(out var item, out var scheduledTime) && scheduledTime <= now)
            {
                if (_queue.TryDequeue(out item, out _))
                {
                    await DispatchToWorkerQueue(item).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Dispatches a task to the worker queue for execution.
        /// </summary>
        private async Task DispatchToWorkerQueue(TaskHandlerExecutor item)
        {
            try
            {
                string queueName = item.QueueName ??
                    (item.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

                _logger.LogDebug("Shard {ShardId}: Dispatching task {TaskId} to queue '{QueueName}'",
                    _shardId, item.PersistenceId, queueName);

                await _queueManager.TryEnqueue(queueName, item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shard {ShardId}: Unable to dispatch task {TaskId}",
                    _shardId, item.PersistenceId);

                if (_taskStorage != null)
                {
                    await _taskStorage.SetStatus(
                        item.PersistenceId,
                        QueuedTaskStatus.Failed,
                        ex,
                        CancellationToken.None
                    ).ConfigureAwait(false);
                }
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
    /// Initializes a new instance of the <see cref="ShardedScheduler"/> class.
    /// </summary>
    /// <param name="queueManager">Worker queue manager for task dispatching.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="taskStorage">Optional task storage for persisting task states.</param>
    /// <param name="shardCount">Number of independent shards. 0 = auto-scale to ProcessorCount (minimum 4).</param>
    public ShardedScheduler(
        IWorkerQueueManager queueManager,
        IEverTaskLogger<ShardedScheduler> logger,
        ITaskStorage? taskStorage = null,
        int shardCount = 0)
    {
        _logger = logger;
        _shardCount = shardCount > 0 ? shardCount : Math.Max(4, Environment.ProcessorCount);

        _logger.LogInformation("Initializing ShardedScheduler with {ShardCount} shards", _shardCount);

        _shards = Enumerable.Range(0, _shardCount)
            .Select(i => new Shard(i, queueManager, logger, taskStorage))
            .ToArray();
    }

    /// <summary>
    /// Schedules a task for execution using hash-based shard distribution.
    /// </summary>
    /// <param name="item">Task handler executor to schedule.</param>
    /// <param name="nextRecurringRun">Next execution time for recurring tasks (overrides item.ExecutionTime).</param>
    public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
    {
        var scheduledTime = nextRecurringRun ?? item.ExecutionTime;
        ArgumentNullException.ThrowIfNull(scheduledTime);

        // Hash-based sharding per distribuzione uniforme
        // Use unsigned hash to prevent negative modulo when GetHashCode() returns int.MinValue
        int shardIndex = (int)((uint)item.PersistenceId.GetHashCode() % (uint)_shardCount);

        _shards[shardIndex].Schedule(item, scheduledTime.Value);
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

using EverTask.Logger;

namespace EverTask.Scheduler;

public class TimerScheduler : IScheduler
{
    private readonly IWorkerQueue _workerQueue;
    private readonly ITaskStorage? _taskStorage;
    private readonly IEverTaskLogger<TimerScheduler> _logger;
    private readonly ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> _queue;
    private readonly Timer _timer;

    public TimerScheduler(IWorkerQueue workerQueue,
                        IEverTaskLogger<TimerScheduler> logger,
                        ITaskStorage? taskStorage = null)
    {
        _workerQueue = workerQueue;
        _taskStorage = taskStorage;
        _logger      = logger;
        _queue       = new ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset>();
        _timer       = new Timer(TimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    internal ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> GetQueue()
    {
        return _queue;
    }

    public void Schedule(TaskHandlerExecutor item)
    {
        ArgumentNullException.ThrowIfNull(item.ExecutionTime);
        _queue.Enqueue(item, item.ExecutionTime.Value);

        // Calcola il prossimo tempo di attivazione del timer
        UpdateTimer();
    }

    internal void TimerCallback(object? state)
    {
        while (_queue.TryPeek(out var item, out DateTimeOffset nextDeliveryTime) &&
               nextDeliveryTime <= DateTimeOffset.UtcNow)
        {
            if (_queue.TryDequeue(out item, out _))
            {
                ProcessItem(item);
            }
        }

        // Update timer for the next event
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (_queue.TryPeek(out _, out DateTimeOffset nextDeliveryTime))
        {
            var delay = nextDeliveryTime - DateTimeOffset.UtcNow;

            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            // There are no items, so disable the timer
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void ProcessItem(TaskHandlerExecutor item)
    {
        DispatcherQueueAsync(item).ConfigureAwait(false);
    }

    internal async Task DispatcherQueueAsync(TaskHandlerExecutor item)
    {
        try
        {
            await _workerQueue.Queue(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to dispatch task with id {taskId}.", item.PersistenceId);
            if (_taskStorage != null)
                await _taskStorage
                      .SetTaskStatus(item.PersistenceId, QueuedTaskStatus.Failed, ex, CancellationToken.None)
                      .ConfigureAwait(false);
        }
    }
}

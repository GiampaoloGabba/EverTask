namespace EverTask.Scheduler;

public class TimerScheduler : IScheduler
{
    private readonly IWorkerQueue _workerQueue;
    private readonly ITaskStorage? _taskStorage;
    private readonly IEverTaskLogger<TimerScheduler> _logger;
    private readonly ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> _queue;
    private readonly Timer _timer;

#if DEBUG
    //for tests purpose
    internal TimeSpan LastSetDelay { get; private set; }
#endif
    public TimerScheduler(IWorkerQueue workerQueue,
                          IEverTaskLogger<TimerScheduler> logger,
                          ITaskStorage? taskStorage = null)
    {
        _workerQueue     = workerQueue;
        _taskStorage     = taskStorage;
        _logger          = logger;
        _queue           = new ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset>();
        _timer           = new Timer(TimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    internal ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset> GetQueue() => _queue;

    public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
    {
        if (item.RecurringTask != null && nextRecurringRun != null)
        {
            _logger.LogWarning("Next run {NextRecurringRun}", nextRecurringRun.Value);
            _queue.Enqueue(item, nextRecurringRun.Value);
            UpdateTimer();
        }
        else
        {
            ArgumentNullException.ThrowIfNull(item.ExecutionTime);
            _queue.Enqueue(item, item.ExecutionTime.Value);
            UpdateTimer();
        }
    }

    internal void TimerCallback(object? state)
    {
        while (_queue.TryPeek(out var item, out var nextDeliveryTime) && nextDeliveryTime <= DateTimeOffset.UtcNow)
        {
            if (_queue.TryDequeue(out item, out _))
                ProcessItem(item);
        }
        UpdateTimer();
    }

    internal void UpdateTimer()
    {
        if (_queue.TryPeek(out var item, out DateTimeOffset nextDeliveryTime))
        {
            try
            {
                var delay = nextDeliveryTime - DateTimeOffset.UtcNow;

                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                //no need to put big timers in queue. just recheck sooner if there is something
                //also the change event is always update when new tasks with big priority are added
                if (delay >= TimeSpan.FromHours(2)) delay = TimeSpan.FromHours(1.5);

#if DEBUG
                //for tests purpose
                LastSetDelay = delay;
#endif
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to set the timer for the next run for task with id {taskId}.", item.PersistenceId);
                _taskStorage?.SetStatus(item.PersistenceId, QueuedTaskStatus.Failed, ex, CancellationToken.None)
                            .ConfigureAwait(false);
            }
        }
        else
        {
            // There are no items, so disable the timer
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }


    //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#timer-callbacks
    private void ProcessItem(TaskHandlerExecutor item) =>
        _ = DispatchToWorkerQueue(item).ConfigureAwait(false);

    internal async Task DispatchToWorkerQueue(TaskHandlerExecutor item)
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
                      .SetStatus(item.PersistenceId, QueuedTaskStatus.Failed, ex, CancellationToken.None)
                      .ConfigureAwait(false);
        }
    }
}

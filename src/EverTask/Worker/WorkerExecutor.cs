using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace EverTask.Worker;

public interface IEverTaskWorkerExecutor
{
    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;
    internal ValueTask DoWork(TaskHandlerExecutor task, CancellationToken serviceToken);
}

public class WorkerExecutor(
    IWorkerBlacklist workerBlacklist,
    EverTaskServiceConfiguration configuration,
    IServiceScopeFactory serviceScopeFactory,
    IScheduler scheduler,
    ICancellationSourceProvider cancellationSourceProvider,
    IEverTaskLogger<WorkerExecutor> logger) : IEverTaskWorkerExecutor
{
    // Performance optimization: Cache for event data to avoid repeated serialization
    private static readonly ConditionalWeakTable<IEverTask, string> TaskJsonCache = new();
    private static readonly ConcurrentDictionary<Type, string> TypeStringCache = new();

    // Performance optimization: Cache handler options to avoid runtime casts per execution
    private static readonly ConcurrentDictionary<Type, HandlerOptionsCache> HandlerOptionsInternalCache = new();

    private record HandlerOptionsCache(IRetryPolicy RetryPolicy, TimeSpan? Timeout);

    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;

    public async ValueTask DoWork(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        //Task storage could be a dbcontext wich is not thread safe.
        //So its safer to just use a new scope for each task
        using var     scope       = serviceScopeFactory.CreateScope();
        ITaskStorage? taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        try
        {
            serviceToken.ThrowIfCancellationRequested();

            if (IsTaskBlacklisted(task))
                return;

            RegisterInfo(task, "Starting task with id {0}.", task.PersistenceId);

            if (taskStorage != null)
                await taskStorage.SetInProgress(task.PersistenceId, serviceToken).ConfigureAwait(false);

            await ExecuteCallback(task.HandlerStartedCallback, task, "Started").ConfigureAwait(false);

            var executionTime = await ExecuteTask(task, serviceToken);

            await ExecuteDispose(task);

            if (taskStorage != null)
                await taskStorage.SetCompleted(task.PersistenceId).ConfigureAwait(false);

            await ExecuteCallback(task.HandlerCompletedCallback, task, "Completed").ConfigureAwait(false);

            RegisterInfo(task, "Task with id {0} was completed in {1} ms.", task.PersistenceId, executionTime);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, task, serviceToken, taskStorage);
        }
        finally
        {
            cancellationSourceProvider.Delete(task.PersistenceId);
            await QueueNextOccourrence(task, taskStorage);
        }
    }

    private bool IsTaskBlacklisted(TaskHandlerExecutor task)
    {
        if (workerBlacklist.IsBlacklisted(task.PersistenceId))
        {
            RegisterInfo(task, "Task with id {0} is signaled to be cancelled and will not be executed.",
                task.PersistenceId);
            workerBlacklist.Remove(task.PersistenceId);
            return true;
        }

        return false;
    }

    private async Task<double> ExecuteTask(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        serviceToken.ThrowIfCancellationRequested();

        var taskToken = cancellationSourceProvider.CreateToken(task.PersistenceId, serviceToken);

        // Performance optimization: Cache handler options to avoid repeated casts
        // Use GetOrAdd overload with factoryArgument to avoid closure allocation
        var options = HandlerOptionsInternalCache.GetOrAdd(
            task.Handler.GetType(),
            static (_, state) =>
            {
                var (handler, config) = state;
                // Cast only once per handler type (first time)
                if (handler is IEverTaskHandlerOptions handlerOptions)
                {
                    return new HandlerOptionsCache(
                        handlerOptions.RetryPolicy ?? config.DefaultRetryPolicy,
                        handlerOptions.Timeout ?? config.DefaultTimeout
                    );
                }

                return new HandlerOptionsCache(
                    config.DefaultRetryPolicy,
                    config.DefaultTimeout
                );
            },
            (task.Handler, configuration));

        var retryPolicy = options.RetryPolicy;
        var timeout = options.Timeout;

        // Use GetTimestamp/GetElapsedTime for .NET 7+ to avoid Stopwatch allocation
#if NET7_0_OR_GREATER
        var startTime = Stopwatch.GetTimestamp();
        await DoExecute();
        var elapsedTime = Stopwatch.GetElapsedTime(startTime);
        return elapsedTime.TotalMilliseconds;
#else
        var stopwatch = Stopwatch.StartNew();
        await DoExecute();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
#endif

        async Task DoExecute()
        {
            //Use WaitAsync for cancelling:
            //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#cancelling-uncancellable-operations
            await retryPolicy.Execute(async retryToken =>
                {
                    if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                    {
                        await ExecuteWithTimeout(
                            innerToken => task.HandlerCallback.Invoke(task.Task, innerToken).WaitAsync(retryToken),
                            timeout.Value,
                            retryToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await task.HandlerCallback.Invoke(task.Task, retryToken).WaitAsync(retryToken)
                                  .ConfigureAwait(false);
                    }
                }
                , logger
                , taskToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteWithTimeout(Func<CancellationToken, Task> action, TimeSpan timeout,
                                          CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        var timeoutToken = timeoutCts.Token;
        try
        {
            await action(timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException();
        }
        finally
        {
            //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#always-dispose-cancellationtokensources-used-for-timeouts
            timeoutCts.Cancel();
        }
    }

    private async Task ExecuteDispose(TaskHandlerExecutor task)
    {
        if (task.Handler is IAsyncDisposable asyncDisposable)
        {
            try
            {
                await asyncDisposable.DisposeAsync();
            }
            catch (Exception e)
            {
                RegisterError(e, task, "Unable to dispose Task with id {0}.", task.PersistenceId);
            }
        }
    }

    private async ValueTask ExecuteCallback(Func<Guid, ValueTask>? handler, TaskHandlerExecutor task,
                                            string callbackName)
    {
        if (handler == null) return;

        try
        {
            await handler.Invoke(task.PersistenceId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            RegisterError(e, task,
                "Error occurred executing while executing the callback override {0} task with id {1}.", callbackName,
                task.PersistenceId);
        }
    }

    private async ValueTask ExecuteCallback(Func<Guid, Exception?, string, ValueTask>? handler,
                                            TaskHandlerExecutor task,
                                            Exception? exception, string message)
    {
        if (handler == null) return;

        try
        {
            await handler.Invoke(task.PersistenceId, exception, message).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            RegisterError(e, task,
                "Error occurred executing while executing the callback override OnError task with id {1}.",
                task.PersistenceId);
        }
    }

    private async Task HandleExceptionAsync(Exception ex, TaskHandlerExecutor task, CancellationToken serviceToken,
                                            ITaskStorage? taskStorage)
    {
        if (ex is OperationCanceledException oce)
        {
            if (taskStorage != null)
            {
                if (serviceToken.IsCancellationRequested)
                    await taskStorage.SetCancelledByService(task.PersistenceId, oce).ConfigureAwait(false);
                else
                    await taskStorage.SetCancelledByUser(task.PersistenceId).ConfigureAwait(false);
            }

            await ExecuteCallback(task.HandlerErrorCallback, task, oce,
                $"Task with id {task.PersistenceId} was cancelled").ConfigureAwait(false);

            RegisterWarning(oce, task, "Task with id {0} was cancelled by service while stopping.", task.PersistenceId);
        }
        else
        {
            // Logica per le altre eccezioni
            if (taskStorage != null)
                await taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, ex, serviceToken)
                                 .ConfigureAwait(false);

            await ExecuteCallback(task.HandlerErrorCallback, task, ex,
                $"Error occurred executing the task with id {task.PersistenceId}").ConfigureAwait(false);

            RegisterError(ex, task, "Error occurred executing task with id {0}.", task.PersistenceId);
        }
    }

    private async Task QueueNextOccourrence(TaskHandlerExecutor task, ITaskStorage? taskStorage)
    {
        if (task.RecurringTask == null) return;

        if (taskStorage != null)
        {
            var currentRun = await taskStorage.GetCurrentRunCount(task.PersistenceId);

            // Fix for schedule drift: Use the scheduled execution time as base for next calculation,
            // not the current time. This ensures recurring tasks maintain their intended schedule
            // even when execution is delayed due to system load or downtime.
            // See: docs/recurring-task-schedule-drift-fix.md
            var scheduledTime = task.ExecutionTime ?? DateTimeOffset.UtcNow;

            // Use extension method to calculate next valid run and get skip information
            var result = task.RecurringTask.CalculateNextValidRun(scheduledTime, currentRun + 1);

            // Log and persist skipped occurrences if any
            if (result.SkippedCount > 0)
            {
                var skippedTimes = string.Join(", ",
                    result.SkippedOccurrences.Select(d => d.ToString("yyyy-MM-dd HH:mm:ss")));

                logger.LogInformation(
                    "Task {TaskId} skipped {SkippedCount} missed occurrence(s) to maintain schedule: {SkippedTimes}",
                    task.PersistenceId, result.SkippedCount, skippedTimes);

                // Persist skip information if storage supports it (EfCore implementation)
                if (taskStorage is EverTask.Storage.EfCore.EfCoreTaskStorage efCoreStorage)
                {
                    await efCoreStorage.RecordSkippedOccurrences(
                        task.PersistenceId,
                        result.SkippedOccurrences,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            await taskStorage.UpdateCurrentRun(task.PersistenceId, result.NextRun)
                             .ConfigureAwait(false);

            if (result.NextRun.HasValue)
                scheduler.Schedule(task, result.NextRun);
        }
    }

    #region Logging and event pubblishing

    private void RegisterInfo(TaskHandlerExecutor executor, string message, params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Information, executor, message, null, messageArgs);

    private void RegisterWarning(Exception? exception, TaskHandlerExecutor executor, string message,
                                 params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Warning, executor, message, exception, messageArgs);

    private void RegisterError(Exception exception, TaskHandlerExecutor executor, string message,
                               params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Error, executor, message, exception, messageArgs);

    private void RegisterEvent(SeverityLevel severity, TaskHandlerExecutor executor, string message,
                               Exception? exception = null, params object[] messageArgs)
    {
        // Format message once for both logging and event publishing
        // This avoids "Message template should be compile time constant" warning
        var formattedMessage = messageArgs.Length > 0
            ? string.Format(message, messageArgs)
            : message;

        switch (severity)
        {
            case SeverityLevel.Information:
                logger.LogInformation(formattedMessage);
                break;
            case SeverityLevel.Warning:
                logger.LogWarning(exception, formattedMessage);
                break;
            case SeverityLevel.Error:
            default:
                logger.LogError(exception, formattedMessage);
                break;
        }

        try
        {
            PublishEvent(executor, severity, formattedMessage, exception);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unable to publish event {Message}", formattedMessage);
        }
    }

    private void PublishEvent(TaskHandlerExecutor task, SeverityLevel severity, string formattedMessage,
                              Exception? exception = null)
    {
        var eventHandlers = TaskEventOccurredAsync?.GetInvocationList();
        if (eventHandlers == null || eventHandlers.Length == 0)
            return;

        // Create event data ONCE outside loop, reuse for all subscribers
        var data = CreateEventDataCached(task, severity, formattedMessage, exception);

        foreach (var eventHandler in eventHandlers)
        {
            var handler = (Func<EverTaskEventData, Task>)eventHandler;

            // Fire and forget with exception handling to prevent unobserved task exceptions
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Event handler failed for task {TaskId}", data.TaskId);
                }
            });
        }
    }

    private EverTaskEventData CreateEventDataCached(TaskHandlerExecutor executor, SeverityLevel severity,
                                                     string message, Exception? exception)
    {
        // Cache task JSON (weak reference - GC'd when task is collected)
        var taskJson = TaskJsonCache.GetValue(executor.Task, JsonConvert.SerializeObject);

        // Cache type strings (permanent cache - types never unload)
        var taskType = TypeStringCache.GetOrAdd(executor.Task.GetType(), type => type.ToString());
        var handlerType = TypeStringCache.GetOrAdd(executor.Handler.GetType(), type => type.ToString());

        return new EverTaskEventData(
            executor.PersistenceId,
            DateTimeOffset.UtcNow,
            severity.ToString(),
            taskType,
            handlerType,
            taskJson,
            message,
            exception?.ToDetailedString()
        );
    }

    #endregion
}

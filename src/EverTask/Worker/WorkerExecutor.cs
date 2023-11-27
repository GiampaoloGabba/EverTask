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

            await ExecuteTask(task, serviceToken);

            await ExecuteDispose(task);

            if (taskStorage != null)
                await taskStorage.SetCompleted(task.PersistenceId).ConfigureAwait(false);

            await ExecuteCallback(task.HandlerCompletedCallback, task, "Completed").ConfigureAwait(false);

            RegisterInfo(task, "Task with id {0} was completed.", task.PersistenceId);
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
            RegisterInfo(task, "Task with id {0} is signaled to be cancelled and will not be executed.", task.PersistenceId);
            workerBlacklist.Remove(task.PersistenceId);
            return true;
        }

        return false;
    }

    private async Task ExecuteTask(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        serviceToken.ThrowIfCancellationRequested();

        var taskToken = cancellationSourceProvider.CreateToken(task.PersistenceId, serviceToken);

        var handlerOptions = task.Handler as IEverTaskHandlerOptions;
        var retryPolicy    = handlerOptions?.RetryPolicy ?? configuration.DefaultRetryPolicy;
        var timeout        = handlerOptions?.Timeout ?? configuration.DefaultTimeout;
        var cpuBound       = handlerOptions?.CpuBoundOperation ?? false;

        if (cpuBound)
        {
            //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#avoid-using-taskrun-for-long-running-work-that-blocks-the-thread
            await (_ = await Task.Factory.StartNew(async () => await DoExecute(),taskToken, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        }
        else
        {
            await DoExecute();
        }

        return;

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
                    await task.HandlerCallback.Invoke(task.Task, retryToken).WaitAsync(retryToken).ConfigureAwait(false);
                }
            }, taskToken).ConfigureAwait(false);
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
                await taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, ex)
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
            var nextRun    = task.RecurringTask.CalculateNextRun(DateTimeOffset.UtcNow, currentRun + 1);
            await taskStorage.UpdateCurrentRun(task.PersistenceId, nextRun)
                             .ConfigureAwait(false);

            if (nextRun.HasValue)
                scheduler.Schedule(task, nextRun);
        }
    }

    #region Logging and event pubblishing

    private void RegisterInfo(TaskHandlerExecutor executor, string message, params object[] messageArgs)
    {
        RegisterEvent(SeverityLevel.Information, executor, message, null, messageArgs);
    }

    private void RegisterWarning(Exception? exception, TaskHandlerExecutor executor, string message,
                                 params object[] messageArgs)
    {
        RegisterEvent(SeverityLevel.Warning, executor, message, exception, messageArgs);
    }

    private void RegisterError(Exception exception, TaskHandlerExecutor executor, string message,
                               params object[] messageArgs)
    {
        RegisterEvent(SeverityLevel.Error, executor, message, exception, messageArgs);
    }

    private void RegisterEvent(SeverityLevel severity, TaskHandlerExecutor executor, string message,
                               Exception? exception = null, params object[] messageArgs)
    {
        switch (severity)
        {
            case SeverityLevel.Information:
                logger.LogInformation(message, messageArgs);
                break;
            case SeverityLevel.Warning:
                logger.LogWarning(exception, message, messageArgs);
                break;
            case SeverityLevel.Error:
            default:
                logger.LogError(exception, message, messageArgs);
                break;
        }

        try
        {
            PublishEvent(executor, severity, message, exception, messageArgs);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unable to publish event {message}", message);
        }
    }

    internal void PublishEvent(TaskHandlerExecutor task, SeverityLevel severity, string message,
                               Exception? exception = null, params object[] messageArgs)
    {
        var eventHandlers = TaskEventOccurredAsync?.GetInvocationList();
        if (eventHandlers == null)
            return;

        message = string.Format(message, messageArgs);

        foreach (var eventHandler in eventHandlers)
        {
            var data    = EverTaskEventData.FromExecutor(task, severity, message, exception);
            var handler = (Func<EverTaskEventData, Task>)eventHandler;
            _ = handler(data);
        }
    }

    #endregion
}

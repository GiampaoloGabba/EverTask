using EverTask.Logger;
using EverTask.Monitoring;

namespace EverTask.Worker;

public interface IEverTaskWorkerExecutor
{
    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;
    internal ValueTask DoWork(TaskHandlerExecutor task, CancellationToken token);
}

public class WorkerExecutor(
    IWorkerBlacklist workerBlacklist,
    EverTaskServiceConfiguration configuration,
    IServiceScopeFactory serviceScopeFactory,
    IEverTaskLogger<WorkerExecutor> logger) : IEverTaskWorkerExecutor
{
    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;

    public async ValueTask DoWork(TaskHandlerExecutor task, CancellationToken token)
    {
        using var scope       = serviceScopeFactory.CreateScope();
        var       taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        try
        {
            token.ThrowIfCancellationRequested();

            if (workerBlacklist.IsBlacklisted(task.PersistenceId))
            {
                RegisterInfo(task, "Task with id {0} is signaled to be cancelled and will not be executed.",
                    task.PersistenceId);
                workerBlacklist.Remove(task.PersistenceId);
                return;
            }

            token.ThrowIfCancellationRequested();

            RegisterInfo(task, "Starting task with id {0}.", task.PersistenceId);

            token.ThrowIfCancellationRequested();

            if (taskStorage != null)
            {
                await taskStorage.SetTaskInProgress(task.PersistenceId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            await ExecuteCallback(task.HandlerStartedCallback, task, "Started").ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            var handlerOptions = task.Handler as IEverTaskHandlerOptions;

            var retryPolicy = handlerOptions?.RetryPolicy ?? configuration.DefaultRetryPolicy;
            var timeout     = handlerOptions?.Timeout ?? configuration.DefaultTimeout;

            await retryPolicy.Execute(async retryToken =>
            {
                if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                {
                    await ExecuteWithTimeout(
                        innerToken => task.HandlerCallback.Invoke(task.Task, innerToken),
                        timeout.Value,
                        retryToken).ConfigureAwait(false);
                }
                else
                {
                    await task.HandlerCallback.Invoke(task.Task, retryToken).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

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

            if (taskStorage != null)
                await taskStorage.SetTaskCompleted(task.PersistenceId).ConfigureAwait(false);

            await ExecuteCallback(task.HandlerCompletedCallback, task, "Completed").ConfigureAwait(false);

            RegisterInfo(task, "Task with id {0} was completed.", task.PersistenceId);
        }
        catch (OperationCanceledException ex)
        {
            if (taskStorage != null)
                await taskStorage.SetTaskCancelledByService(task.PersistenceId, ex).ConfigureAwait(false);

            await ExecuteCallback(task.HandlerErrorCallback, task, ex,
                $"Task with id {task.PersistenceId} was cancelled").ConfigureAwait(false);

            RegisterWarning(ex, task, "Task with id {0} was cancelled by service while stopping.", task.PersistenceId);
        }
        catch (Exception ex)
        {
            if (taskStorage != null)
                await taskStorage.SetTaskStatus(task.PersistenceId, QueuedTaskStatus.Failed, ex)
                                 .ConfigureAwait(false);

            await ExecuteCallback(task.HandlerErrorCallback, task, ex,
                $"Error occurred executing the task with id {task.PersistenceId}").ConfigureAwait(false);

            RegisterError(ex, task, "Error occurred executing task with id {0}.", task.PersistenceId);
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
}

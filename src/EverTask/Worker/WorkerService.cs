using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Monitoring;
using Microsoft.Extensions.Hosting;

namespace EverTask.Worker;

public interface IEverTaskWorkerService
{
    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;
    Task StartAsync(CancellationToken stoppingToken);
    Task StopAsync(CancellationToken stoppingToken);
}

public class WorkerService(
    IWorkerQueue workerQueue,
    IServiceScopeFactory serviceScopeFactory,
    ITaskDispatcherInternal taskDispatcher,
    EverTaskServiceConfiguration configuration,
    IWorkerBlacklist workerBlacklist,
    IEverTaskLogger<WorkerService> logger) : BackgroundService, IEverTaskWorkerService
{

    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogTrace("EverTask BackgroundService is running.");
        logger.LogTrace("MaxDegreeOfParallelism: {maxDegreeOfParallelism}", configuration.MaxDegreeOfParallelism);

        await ProcessPendingAsync(ct).ConfigureAwait(false);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = configuration.MaxDegreeOfParallelism,
            CancellationToken      = ct
        };

        await Parallel.ForEachAsync(workerQueue.DequeueAll(ct), options, DoWork).ConfigureAwait(false);
    }

    private async ValueTask DoWork(TaskHandlerExecutor task, CancellationToken token)
    {
        using var scope       = serviceScopeFactory.CreateScope();
        var       taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        try
        {
            if (workerBlacklist.IsBlacklisted(task.PersistenceId))
            {
                RegisterInfo(task, "Task with id {0} is signaled to be cancelled and will not be executed.", task.PersistenceId);
                workerBlacklist.Remove(task.PersistenceId);
                return;
            }

            RegisterInfo(task, "Starting task with id {0}.", task.PersistenceId);

            if (taskStorage != null)
                await taskStorage.SetTaskInProgress(task.PersistenceId, token).ConfigureAwait(false);

            if (task.HandlerStartedCallback != null)
                await task.HandlerStartedCallback.Invoke(task.PersistenceId).ConfigureAwait(false);

            await task.HandlerCallback.Invoke(task.Task, token).ConfigureAwait(false);

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
                await taskStorage.SetTaskCompleted(task.PersistenceId, token).ConfigureAwait(false);

            if (task.HandlerCompletedCallback != null)
            {
                await task.HandlerCompletedCallback.Invoke(task.PersistenceId).ConfigureAwait(false);
            }

            RegisterInfo(task, "Task with id {0} was completed.", task.PersistenceId);
        }
        catch (OperationCanceledException ex)
        {
            if (taskStorage != null)
                await taskStorage.SetTaskStatus(task.PersistenceId, QueuedTaskStatus.Cancelled, null, token)
                                 .ConfigureAwait(false);

            if (task.HandlerErrorCallback != null)
            {
                await task.HandlerErrorCallback
                          .Invoke(task.PersistenceId, ex, $"Task with id {task.PersistenceId} was cancelled")
                          .ConfigureAwait(false);
            }

            RegisterWarning(ex, task, "Task with id {0} was cancelled.", task.PersistenceId);
        }
        catch (Exception ex)
        {
            if (taskStorage != null)
                await taskStorage.SetTaskStatus(task.PersistenceId, QueuedTaskStatus.Failed, ex, token)
                                 .ConfigureAwait(false);

            if (task.HandlerErrorCallback != null)
            {
                await task.HandlerErrorCallback
                          .Invoke(task.PersistenceId, ex, $"Error occurred executing the task with id {task.PersistenceId}")
                          .ConfigureAwait(false);
            }

            RegisterError(ex, task, "Error occurred executing task with id {0}.", task.PersistenceId);
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        using var scope       = serviceScopeFactory.CreateScope();
        var       taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        if (taskStorage == null)
        {
            logger.LogWarning(
                "Persistence is not active. In your DI, use .AddSqlStorage() for persistent tasks or .AddMemoryStorage() for tests");
            return;
        }

        var pendingTasks = await taskStorage.RetrievePendingTasks(ct).ConfigureAwait(false);
        var contTask     = 0;
        logger.LogTrace("Found {count} tasks to execute", pendingTasks.Length);

        foreach (var taskInfo in pendingTasks)
        {
            contTask++;
            logger.LogTrace("Processing task {task} of {count} tasks to execute", contTask, pendingTasks.Length);
            IEverTask? task = null;

            try
            {
                var type = Type.GetType(taskInfo.Type);
                if (type != null && typeof(IEverTask).IsAssignableFrom(type))
                {
                    task = (IEverTask?)JsonConvert.DeserializeObject(taskInfo.Request, type);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to deserialize task with id {taskId}.", taskInfo.Id);
            }

            if (task != null)
            {
                try
                {
                    await taskDispatcher.ExecuteDispatch(task, taskInfo.ScheduledExecutionUtc, ct, taskInfo.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await taskStorage.SetTaskStatus(taskInfo.Id, QueuedTaskStatus.Failed, ex, ct).ConfigureAwait(false);
                    logger.LogError(ex, "Error occurred executing task with id {taskId}.", taskInfo.Id);
                }
            }
            else
            {
                await taskStorage.SetTaskStatus(
                    taskInfo.Id,
                    QueuedTaskStatus.Failed,
                    new Exception("Unable to create the IBackground task from the specified properties"),
                    ct).ConfigureAwait(false);
            }
        }
    }

    private void RegisterInfo(TaskHandlerExecutor executor, string message, params object[] messageArgs)
    {
        RegisterEvent(SeverityLevel.Information, executor, message, null, messageArgs);
    }

    private void RegisterWarning(Exception? exception, TaskHandlerExecutor executor, string message, params object[] messageArgs)
    {
        RegisterEvent(SeverityLevel.Warning, executor, message, exception, messageArgs);
    }

    private void RegisterError(Exception exception, TaskHandlerExecutor executor, string message, params object[] messageArgs)
    {
        RegisterEvent(SeverityLevel.Error, executor, message, exception, messageArgs);
    }

    private void RegisterEvent(SeverityLevel severity, TaskHandlerExecutor executor, string message, Exception? exception = null, params object[] messageArgs)
    {
        if (severity == SeverityLevel.Information)
        {
            logger.LogInformation(message, messageArgs);
        }
        else if (severity == SeverityLevel.Warning)
        {
            logger.LogWarning(exception, message, messageArgs);
        }
        else
        {
            logger.LogError(exception, message, messageArgs);
        }

        PublishEvent(executor,severity, message, exception, messageArgs);
    }

    internal void PublishEvent(TaskHandlerExecutor task, SeverityLevel severity, string message, Exception? exception = null, params object[] messageArgs)
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

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EverTask BackgroundService is stopping.");
        await base.StopAsync(stoppingToken);
    }
}

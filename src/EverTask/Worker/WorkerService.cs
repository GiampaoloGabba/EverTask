using EverTask.Logger;
using Microsoft.Extensions.Hosting;

namespace EverTask.Worker;

public class WorkerService(
    IWorkerQueue workerQueue,
    IServiceScopeFactory serviceScopeFactory,
    ITaskDispatcher taskDispatcher,
    EverTaskServiceConfiguration configuration,
    IEverTaskLogger<WorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Queued BackgroundService is running, searching for pending tasks to execute...");

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
            if (taskStorage != null)
                await taskStorage.SetTaskInProgress(task.PersistenceId, token).ConfigureAwait(false);

            await task.HandlerCallback.Invoke(task.Task, token).ConfigureAwait(false);

            if (taskStorage != null)
                await taskStorage.SetTaskCompleted(task.PersistenceId, token).ConfigureAwait(false);

            if (task.HandlerCompletedCallback != null)
            {
                await task.HandlerCompletedCallback.Invoke().ConfigureAwait(false);
            }

            logger.LogInformation("Task with id {taskId} was completed.", task.PersistenceId);
        }
        catch (OperationCanceledException ex)
        {
            if (taskStorage != null)
                await taskStorage.SetTaskStatus(task.PersistenceId, QueuedTaskStatus.Cancelled, null, token)
                                 .ConfigureAwait(false);

            if (task.HandlerErrorCallback != null)
            {
                await task.HandlerErrorCallback
                          .Invoke(ex, $"Task with id {task.PersistenceId} was cancelled")
                          .ConfigureAwait(false);
            }

            logger.LogWarning(ex, "Task with id {taskId} was cancelled.", task.PersistenceId);
        }
        catch (Exception ex)
        {
            if (taskStorage != null)
                await taskStorage.SetTaskStatus(task.PersistenceId, QueuedTaskStatus.Failed, ex, token)
                                 .ConfigureAwait(false);

            if (task.HandlerErrorCallback != null)
            {
                await task.HandlerErrorCallback
                          .Invoke(ex, $"Error occurred executing task with id {task.PersistenceId}")
                          .ConfigureAwait(false);
            }

            logger.LogError(ex, "Error occurred executing task with id {taskId}.", task.PersistenceId);
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
        logger.LogInformation("Found {count} tasks to execute", pendingTasks.Length);

        foreach (var taskInfo in pendingTasks)
        {
            contTask++;
            logger.LogInformation("Processing task {task} of {count} tasks to execute", contTask, pendingTasks.Length);
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
                    await taskDispatcher.ExecuteDispatch(task, ct, taskInfo.Id).ConfigureAwait(false);
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

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queued BackgroundService is stopping.");
        await base.StopAsync(stoppingToken);
    }
}

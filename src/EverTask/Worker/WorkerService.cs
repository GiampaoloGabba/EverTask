using Microsoft.Extensions.Hosting;

namespace EverTask.Worker;

public class WorkerService(
    IWorkerQueue workerQueue,
    IServiceScopeFactory serviceScopeFactory,
    ITaskDispatcherInternal taskDispatcher,
    EverTaskServiceConfiguration configuration,
    IEverTaskWorkerExecutor workerExecutor,
    IEverTaskLogger<WorkerService> logger) : BackgroundService
{
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

        await Parallel.ForEachAsync(workerQueue.DequeueAll(ct), options, workerExecutor.DoWork).ConfigureAwait(false);
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

        var pendingTasks = await taskStorage.RetrievePending(ct).ConfigureAwait(false);
        var contTask     = 0;
        logger.LogTrace("Found {count} tasks to execute", pendingTasks.Length);

        foreach (var taskInfo in pendingTasks)
        {
            contTask++;
            logger.LogTrace("Processing task {task} of {count} tasks to execute", contTask, pendingTasks.Length);
            IEverTask?     task          = null;
            RecurringTask? scheduledTask = null;

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

            try
            {
                if (!string.IsNullOrEmpty(taskInfo.RecurringTask))
                {
                    scheduledTask = JsonConvert.DeserializeObject<RecurringTask>(taskInfo.RecurringTask);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to deserialize recurring task info with id {taskId}.", taskInfo.Id);
            }

            if (task != null)
            {
                try
                {
                    await taskDispatcher.ExecuteDispatch(task, taskInfo.ScheduledExecutionUtc, scheduledTask, taskInfo.CurrentRunCount, ct, taskInfo.Id)
                                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await taskStorage.SetStatus(taskInfo.Id, QueuedTaskStatus.Failed, ex, ct).ConfigureAwait(false);
                    logger.LogError(ex, "Error occurred executing task with id {taskId}.", taskInfo.Id);
                }
            }
            else
            {
                await taskStorage.SetStatus(
                    taskInfo.Id,
                    QueuedTaskStatus.Failed,
                    new Exception("Unable to create the IBackground task from the specified properties"),
                    ct).ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EverTask BackgroundService is stopping.");
        await base.StopAsync(stoppingToken);
    }
}

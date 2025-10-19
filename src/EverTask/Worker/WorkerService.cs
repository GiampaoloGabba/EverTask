using Microsoft.Extensions.Hosting;

namespace EverTask.Worker;

public class WorkerService(
    IWorkerQueueManager queueManager,
    IServiceScopeFactory serviceScopeFactory,
    ITaskDispatcherInternal taskDispatcher,
    EverTaskServiceConfiguration configuration,
    IEverTaskWorkerExecutor workerExecutor,
    IEverTaskLogger<WorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogTrace("EverTask BackgroundService is running");

        // Warn if using suboptimal MaxDegreeOfParallelism configuration
        if (configuration.MaxDegreeOfParallelism == 1)
        {
            var recommendedParallelism = Math.Max(4, Environment.ProcessorCount * 2);
            logger.LogWarning(
                "MaxDegreeOfParallelism is set to 1, which severely limits throughput. " +
                "For production workloads, consider increasing to {RecommendedParallelism} (ProcessorCount * 2) or higher. " +
                "Use SetMaxDegreeOfParallelism() in AddEverTask configuration",
                recommendedParallelism);
        }

        // Process pending tasks from storage
        await ProcessPendingAsync(ct).ConfigureAwait(false);

        // Get all configured queues
        var queues = queueManager.GetAllQueues().ToList();
        logger.LogInformation("Starting consumption of {QueueCount} queue(s): {QueueNames}",
            queues.Count, string.Join(", ", queues.Select(q => q.Name)));

        // Create consumption tasks for each queue with its own parallelism
        var queueConsumptionTasks = queues
            .Select(q => ConsumeQueue(q.Name, q.Queue, ct))
            .ToList();

        // Run all queue consumers concurrently
        await Task.WhenAll(queueConsumptionTasks).ConfigureAwait(false);
    }

    private async Task ConsumeQueue(string queueName, IWorkerQueue queue, CancellationToken ct)
    {
        // Get the configuration for this specific queue
        var queueConfig = queue switch
        {
            WorkerQueue wq => wq.Configuration,
            _ => new Configuration.QueueConfiguration
            {
                Name = queueName,
                MaxDegreeOfParallelism = configuration.MaxDegreeOfParallelism
            }
        };

        logger.LogTrace("Starting consumption of queue '{QueueName}' with MaxDegreeOfParallelism: {MaxDegreeOfParallelism}",
            queueName, queueConfig.MaxDegreeOfParallelism);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = queueConfig.MaxDegreeOfParallelism,
            CancellationToken = ct
        };

        try
        {
            await Parallel.ForEachAsync(queue.DequeueAll(ct), options, workerExecutor.DoWork).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Queue '{QueueName}' consumption cancelled", queueName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in queue '{QueueName}' consumption", queueName);
            throw;
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

        var pendingTasks = await taskStorage.RetrievePending(ct).ConfigureAwait(false);
        logger.LogInformation("Found {Count} pending tasks to process", pendingTasks.Length);

        if (pendingTasks.Length == 0)
            return;

        // Process pending tasks with bounded parallelism to improve startup time
        // Use configured MaxDegreeOfParallelism to respect user settings
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = configuration.MaxDegreeOfParallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(pendingTasks, options, async (taskInfo, token) =>
        {
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
                logger.LogError(e, "Unable to deserialize task with id {TaskId}", taskInfo.Id);
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
                logger.LogError(e, "Unable to deserialize recurring task info with id {TaskId}", taskInfo.Id);
            }

            if (task != null)
            {
                try
                {
                    await taskDispatcher.ExecuteDispatch(task, taskInfo.ScheduledExecutionUtc, scheduledTask,
                        taskInfo.CurrentRunCount, token, taskInfo.Id)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Create scope per iteration for thread safety (required for DbContext-based storage)
                    using var itemScope = serviceScopeFactory.CreateScope();
                    var itemStorage = itemScope.ServiceProvider.GetService<ITaskStorage>();

                    if (itemStorage != null)
                    {
                        await itemStorage.SetStatus(taskInfo.Id, QueuedTaskStatus.Failed, ex, token)
                            .ConfigureAwait(false);
                    }

                    logger.LogError(ex, "Error occurred executing pending task with id {TaskId}", taskInfo.Id);
                }
            }
            else
            {
                // Create scope per iteration for thread safety (required for DbContext-based storage)
                using var itemScope = serviceScopeFactory.CreateScope();
                var itemStorage = itemScope.ServiceProvider.GetService<ITaskStorage>();

                if (itemStorage != null)
                {
                    await itemStorage.SetStatus(
                        taskInfo.Id,
                        QueuedTaskStatus.Failed,
                        new Exception("Unable to create the IBackground task from the specified properties"),
                        token).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EverTask BackgroundService is stopping");
        await base.StopAsync(stoppingToken);
    }
}

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

        // Create N dedicated consumers for each queue using the official Microsoft pattern
        // This is the recommended approach for channel-based background workers
        var queueConsumptionTasks = queues
            .SelectMany(q => StartConsumers(q.Name, q.Queue, ct))
            .ToList();

        // Wait for all consumers to complete
        await Task.WhenAll(queueConsumptionTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts N dedicated long-lived consumers for a queue.
    /// This is the official Microsoft-recommended pattern for channel consumption.
    /// Benefits over semaphore+list approach:
    /// - Zero per-item allocation (no Task.Run per task)
    /// - Stable memory footprint (N fixed workers)
    /// - No manual task list management
    /// - Natural backpressure with bounded channels
    /// - Graceful shutdown via cancellation token
    /// </summary>
    private IEnumerable<Task> StartConsumers(string queueName, IWorkerQueue queue, CancellationToken ct)
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

        var consumerCount = queueConfig.MaxDegreeOfParallelism;

        logger.LogTrace("Starting {ConsumerCount} dedicated consumer(s) for queue '{QueueName}'",
            consumerCount, queueName);

        // Spawn N long-lived consumers that compete for items from the channel
        for (int i = 0; i < consumerCount; i++)
        {
            var consumerId = i; // Capture for logging
            yield return Task.Run(async () =>
            {
                logger.LogTrace("Consumer #{ConsumerId} for queue '{QueueName}' started",
                    consumerId, queueName);

                try
                {
                    await ConsumeAsync(queue, queueName, consumerId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Consumer #{ConsumerId} for queue '{QueueName}' cancelled",
                        consumerId, queueName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Consumer #{ConsumerId} for queue '{QueueName}' faulted",
                        consumerId, queueName);
                    throw;
                }
                finally
                {
                    logger.LogTrace("Consumer #{ConsumerId} for queue '{QueueName}' stopped",
                        consumerId, queueName);
                }
            }, ct);
        }
    }

    /// <summary>
    /// Long-lived consumer loop that processes items from the queue.
    /// Each consumer competes with others for items from the same channel.
    /// </summary>
    private async Task ConsumeAsync(IWorkerQueue queue, string queueName, int consumerId, CancellationToken ct)
    {
        // Multiple consumers will compete for items from DequeueAll
        // Channel guarantees each item is delivered to exactly one consumer
        await foreach (var task in queue.DequeueAll(ct).ConfigureAwait(false))
        {
            try
            {
                // Direct execution - no Task.Run overhead, no semaphore, no list management
                // Worker is already running in its own Task from StartConsumers
                await workerExecutor.DoWork(task, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown - service is stopping
                logger.LogTrace("Consumer #{ConsumerId} for queue '{QueueName}' received cancellation during task execution",
                    consumerId, queueName);
                return;
            }
            catch (Exception ex)
            {
                // DoWork should handle errors internally, but catch defensively
                // Don't let one task failure kill the entire consumer
                logger.LogError(ex, "Consumer #{ConsumerId} for queue '{QueueName}' error processing task {TaskId}",
                    consumerId, queueName, task.PersistenceId);
                // Continue consuming next item
            }
        }

        logger.LogTrace("Consumer #{ConsumerId} for queue '{QueueName}' exited (channel completed)",
            consumerId, queueName);
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

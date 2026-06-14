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

        // Surface handler-registration diagnostics collected during assembly scanning (duplicate
        // closed handlers, unsupported open-generic handlers) — see HandlerRegistrar (G1/G2).
        foreach (var warning in configuration.HandlerRegistrationWarnings)
        {
            logger.LogWarning("Handler registration: {Warning}", warning);
        }

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

        // Get all configured queues
        var queues = queueManager.GetAllQueues().ToList();
        logger.LogInformation("Starting consumption of {QueueCount} queue(s): {QueueNames}",
            queues.Count, string.Join(", ", queues.Select(q => q.Name)));

        // Create N dedicated consumers for each queue using the official Microsoft pattern
        // This is the recommended approach for channel-based background workers
        var queueConsumptionTasks = queues
            .SelectMany(q => StartConsumers(q.Name, q.Queue, ct))
            .ToList();

        // Recover pending tasks from storage CONCURRENTLY with the consumers.
        // Consumers must already be draining the bounded queues while recovery re-enqueues:
        // otherwise a backlog larger than a queue's capacity deadlocks the startup
        // (recovery blocks on a full channel that nobody is consuming yet).
        queueConsumptionTasks.Add(RunRecoveryAsync(ct));

        // Wait for all consumers (and the recovery) to complete
        await Task.WhenAll(queueConsumptionTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the startup recovery, isolating its failures from the queue consumers:
    /// a recovery error must never stop task consumption.
    /// </summary>
    private async Task RunRecoveryAsync(CancellationToken ct)
    {
        try
        {
            await ProcessPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Pending task recovery cancelled by host shutdown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pending task recovery failed. Queue consumers keep running; " +
                                "unrecovered tasks will be retried at the next startup");
        }
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

        // Clamp to at least one consumer: a queue with zero consumers and FullMode=Wait deadlocks
        // every producer (dispatch, scheduler, recovery) once its channel fills (F5).
        var consumerCount = queueConfig.MaxDegreeOfParallelism;
        if (consumerCount < 1)
        {
            logger.LogWarning(
                "Queue '{QueueName}' is configured with MaxDegreeOfParallelism={Configured} (< 1): " +
                "clamped to 1 consumer to avoid a startup deadlock", queueName, consumerCount);
            consumerCount = 1;
        }

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

        // Process pending tasks using keyset pagination to avoid loading large backlogs into memory
        const int pageSize = 100;
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;
        int totalProcessed = 0;

        // Recovery runs concurrently with live dispatching: only recover tasks created BEFORE
        // this point, so tasks dispatched while recovery is paginating are not re-enqueued twice.
        var recoveryCutoff = DateTimeOffset.UtcNow;

        while (true)
        {
            var page = await taskStorage.RetrievePending(lastCreatedAt, lastId, pageSize, ct).ConfigureAwait(false);

            if (page.Length == 0)
                break;

            // The cutoff is a cheap first pass, not the correctness defense: a re-delivery that
            // slips past it (same-instant tie, stale page) is rejected deterministically at the
            // channel write by the TaskDeliveryRegistry, and a row that terminally finished
            // since the page read is refused by the conditional SetQueued (QueueForRecovery).
            var pendingTasks = page.Where(t => t.CreatedAtUtc <= recoveryCutoff).ToArray();

            logger.LogInformation("Processing batch with {Count} pending tasks (lastCreatedAt={LastCreatedAt}, lastId={LastId})",
                pendingTasks.Length, lastCreatedAt, lastId);

            // Process pending tasks with bounded parallelism to improve startup time
            // Use configured MaxDegreeOfParallelism to respect user settings
            var options = new ParallelOptions
            {
                // Clamp to >= 1: ParallelOptions rejects 0, and a misconfigured zero must never abort
                // recovery (F5).
                MaxDegreeOfParallelism = Math.Max(1, configuration.MaxDegreeOfParallelism),
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

            // Get audit level from task info (null means Full for backward compatibility)
            var auditLevel = taskInfo.AuditLevel.HasValue ? (AuditLevel)taskInfo.AuditLevel.Value : AuditLevel.Full;

            if (task != null)
            {
                try
                {
                    // For recurring tasks, use NextRunUtc if available (preserves schedule after restart)
                    // Fall back to ScheduledExecutionUtc for non-recurring or first-time tasks
                    var executionTime = taskInfo.NextRunUtc ?? taskInfo.ScheduledExecutionUtc;

                    // isRecovery: recovery must never drop tasks, so full queues exert
                    // backpressure here (consumers are draining concurrently), the stored
                    // NextRunUtc is preserved for recurring tasks and the task definition
                    // is not rewritten in storage
                    await taskDispatcher.ExecuteDispatch(task, executionTime, scheduledTask,
                        taskInfo.CurrentRunCount, token, taskInfo.Id, isRecovery: true)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Host shutdown: leave the task in its recoverable status so the next
                    // startup picks it up again. Marking it Failed here would lose it.
                    throw;
                }
                catch (Exception ex)
                {
                    // Do NOT mark the task Failed: the typical failure here is transient
                    // (storage hiccup) and Failed would make a one-shot task permanently
                    // unrecoverable. Leaving the status untouched keeps the task visible to
                    // the next startup recovery; genuinely poison tasks (broken payload) are
                    // already marked Failed by the deserialization branch below.
                    logger.LogError(ex,
                        "Error occurred re-dispatching pending task with id {TaskId}. " +
                        "The task remains in a recoverable status and will be retried at the next startup",
                        taskInfo.Id);
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
                        auditLevel,
                        null,
                        token).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);

            totalProcessed += pendingTasks.Length;

            // Advance the keyset cursor using the RAW page (not the filtered one) so pagination
            // always makes progress; stop once the page goes past the recovery cutoff.
            var lastTask = page[^1];
            lastCreatedAt = lastTask.CreatedAtUtc;
            lastId = lastTask.Id;

            if (lastTask.CreatedAtUtc > recoveryCutoff)
                break;
        }

        logger.LogInformation("Completed processing {TotalCount} pending tasks", totalProcessed);
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EverTask BackgroundService is stopping");
        await base.StopAsync(stoppingToken);
    }
}

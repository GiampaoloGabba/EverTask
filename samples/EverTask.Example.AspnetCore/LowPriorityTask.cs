using EverTask.Abstractions;

namespace EverTask.Example.AspnetCore;

/// <summary>
/// Low-priority background task (e.g., data cleanup, report generation, bulk processing)
/// Routed to low-priority queue with limited parallelism to avoid consuming resources
/// </summary>
public record LowPriorityTask(
    string JobType,
    int ItemCount,
    int ProcessingTimePerItemMs = 100
) : IEverTask;

public class LowPriorityTaskHandler : EverTaskHandler<LowPriorityTask>
{
    public override string QueueName => "low-priority";

    public override async Task Handle(LowPriorityTask task, CancellationToken cancellationToken)
    {
        Logger.LogInformation("LOW PRIORITY: Starting background job '{JobType}' processing {ItemCount} items",
            task.JobType, task.ItemCount);

        Logger.LogDebug("Processing on low-priority queue with parallelism=2 (resource-limited)");

        // Simulate CPU-intensive background work
        for (int i = 1; i <= task.ItemCount; i++)
        {
            await Task.Delay(task.ProcessingTimePerItemMs, cancellationToken);

            if (i % 5 == 0)
            {
                Logger.LogInformation("LOW PRIORITY: Processed {Processed}/{Total} items for job '{JobType}'",
                    i, task.ItemCount, task.JobType);
            }
        }

        Logger.LogInformation("LOW PRIORITY: Completed job '{JobType}' - processed {ItemCount} items in {Duration}ms",
            task.JobType, task.ItemCount, task.ItemCount * task.ProcessingTimePerItemMs);
    }

    public override ValueTask OnStarted(Guid persistenceId)
    {
        Logger.LogInformation("=== STARTED (LOW-PRIORITY): {JobType} - Task {TaskId} ===",
            nameof(LowPriorityTask), persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid persistenceId)
    {
        Logger.LogInformation("=== COMPLETED (LOW-PRIORITY): Task {TaskId} ===", persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        Logger.LogError(exception, "=== FAILED (LOW-PRIORITY): Task {TaskId} - {Message} ===",
            persistenceId, message);
        return ValueTask.CompletedTask;
    }
}

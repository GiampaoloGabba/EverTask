using EverTask.Abstractions;

namespace EverTask.Example.AspnetCore;

/// <summary>
/// High-priority critical task (e.g., payment processing, order confirmation)
/// Routed to high-priority queue with more parallelism and dedicated capacity
/// </summary>
public record HighPriorityTask(
    string Operation,
    string EntityId,
    int ProcessingTimeMs = 500
) : IEverTask;

public class HighPriorityTaskHandler : EverTaskHandler<HighPriorityTask>
{
    public override string QueueName => "high-priority";

    public override async Task Handle(HighPriorityTask task, CancellationToken cancellationToken)
    {
        Logger.LogInformation("HIGH PRIORITY: Starting critical operation '{Operation}' for entity {EntityId}",
            task.Operation, task.EntityId);

        Logger.LogDebug("Processing on high-priority queue with parallelism=10");

        // Simulate critical operation (payment, order confirmation, etc.)
        await Task.Delay(task.ProcessingTimeMs, cancellationToken);

        Logger.LogInformation("HIGH PRIORITY: Completed operation '{Operation}' for entity {EntityId} in {Duration}ms",
            task.Operation, task.EntityId, task.ProcessingTimeMs);
    }

    public override ValueTask OnStarted(Guid persistenceId)
    {
        Logger.LogInformation("=== STARTED (HIGH-PRIORITY): {Operation} - Task {TaskId} ===",
            nameof(HighPriorityTask), persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid persistenceId)
    {
        Logger.LogInformation("=== COMPLETED (HIGH-PRIORITY): Task {TaskId} ===", persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        Logger.LogError(exception, "=== FAILED (HIGH-PRIORITY): Task {TaskId} - {Message} ===",
            persistenceId, message);
        return ValueTask.CompletedTask;
    }
}

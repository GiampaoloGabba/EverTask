using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Lightweight task information for list views.
/// </summary>
public record TaskListDto(
    Guid Id,
    string Type,                          // Short type name (not assembly qualified)
    QueuedTaskStatus Status,
    string? QueueName,
    string? TaskKey,                      // Optional unique identifier for task deduplication
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    DateTimeOffset? ScheduledExecutionUtc,
    bool IsRecurring,
    string? RecurringInfo,                // Human-readable schedule (e.g., "Every 5 minutes")
    int? CurrentRunCount,
    int? MaxRuns,
    double ExecutionTimeMs                // Last execution time in milliseconds
);

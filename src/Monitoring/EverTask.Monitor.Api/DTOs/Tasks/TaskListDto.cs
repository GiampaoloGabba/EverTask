using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Lightweight task information for list views.
/// </summary>
/// <param name="Id">The unique identifier of the task.</param>
/// <param name="Type">The task type (short name, not assembly qualified).</param>
/// <param name="Status">The current task status.</param>
/// <param name="QueueName">The queue name (null for default queue).</param>
/// <param name="TaskKey">Optional unique identifier for task deduplication.</param>
/// <param name="CreatedAtUtc">When the task was created.</param>
/// <param name="LastExecutionUtc">When the task was last executed.</param>
/// <param name="ScheduledExecutionUtc">When the task is scheduled to execute.</param>
/// <param name="IsRecurring">Indicates whether this is a recurring task.</param>
/// <param name="RecurringInfo">Human-readable schedule description (e.g., "Every 5 minutes").</param>
/// <param name="CurrentRunCount">Number of times the task has been executed.</param>
/// <param name="MaxRuns">Maximum number of times the task should run.</param>
/// <param name="ExecutionTimeMs">Last execution time in milliseconds.</param>
public record TaskListDto(
    Guid Id,
    string Type,
    QueuedTaskStatus Status,
    string? QueueName,
    string? TaskKey,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    DateTimeOffset? ScheduledExecutionUtc,
    bool IsRecurring,
    string? RecurringInfo,
    int? CurrentRunCount,
    int? MaxRuns,
    double ExecutionTimeMs
);

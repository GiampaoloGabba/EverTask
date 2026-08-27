namespace EverTask.Storage;

// EventId range 1600–1699 (MemoryTaskStorage). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
//
// Levels mirror the EF Core storage 1:1, so switching Memory ↔ SQL never changes log volume: the
// per-task operations (persist, status transition, run counters, taskKey update, removal, execution
// logs) are Debug; only the recovery page read and the terminal recurring outcomes stay Information.
internal static partial class MemoryTaskStorageLog
{
    [LoggerMessage(EventId = 1600, Level = LogLevel.Debug,
        Message = "Persist Task: {TaskType}")]
    public static partial void TaskPersisted(this ILogger logger, string taskType);

    [LoggerMessage(EventId = 1601, Level = LogLevel.Information,
        Message = "Retrieve Pending Tasks (keyset: lastCreatedAt={LastCreatedAt}, lastId={LastId}, take={Take})")]
    public static partial void RetrievingPendingTasks(this ILogger logger, DateTimeOffset? lastCreatedAt,
                                                      Guid? lastId, int take);

    [LoggerMessage(EventId = 1602, Level = LogLevel.Debug,
        Message = "Task {TaskId} is no longer recoverable, skipping SetQueued")]
    public static partial void TaskNoLongerRecoverable(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1603, Level = LogLevel.Debug,
        Message = "Set Task {TaskId} with Status {Status}")]
    public static partial void StatusSet(this ILogger logger, Guid taskId, QueuedTaskStatus status);

    [LoggerMessage(EventId = 1605, Level = LogLevel.Debug,
        Message = "Update the current run counter for Task {TaskId}")]
    public static partial void UpdatingCurrentRunCount(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1606, Level = LogLevel.Debug,
        Message = "Complete recurring run for Task {TaskId}")]
    public static partial void CompletingRecurringRun(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1607, Level = LogLevel.Information,
        Message = "Finalize recurring series (terminal skip) for Task {TaskId}")]
    public static partial void FinalizingRecurringSeries(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1608, Level = LogLevel.Information,
        Message = "Poison recurring Task {TaskId} terminally")]
    public static partial void PoisoningRecurringTask(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1609, Level = LogLevel.Debug,
        Message = "Updating task {TaskId} with key {TaskKey}")]
    public static partial void UpdatingTask(this ILogger logger, Guid taskId, string? taskKey);

    [LoggerMessage(EventId = 1610, Level = LogLevel.Warning,
        Message = "Task {TaskId} not found for update")]
    public static partial void TaskNotFoundForUpdate(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1611, Level = LogLevel.Debug,
        Message = "Removing task {TaskId}")]
    public static partial void RemovingTask(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1612, Level = LogLevel.Debug,
        Message = "Saving {Count} execution logs for task {TaskId}")]
    public static partial void SavingExecutionLogs(this ILogger logger, int count, Guid taskId);
}

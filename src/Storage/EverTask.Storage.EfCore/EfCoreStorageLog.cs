using Microsoft.Extensions.Logging;

namespace EverTask.Storage.EfCore;

// EventId range 2000-2099 (EfCoreTaskStorage and the four first-party providers). Ranges are allocated per
// component in the #32 plan; a reflection test asserts solution-wide uniqueness.
//
// SqlServer/Postgres/MySql override the three hot writes (SetStatus, UpdateCurrentRun, CompleteRecurringRun)
// and Sqlite overrides RetrievePending/TrySetQueuedIfRecoverable, but they all call THESE definitions with
// their own logger: the category (EverTask.Storage.SqlServer.SqlServerTaskStorage, ...) already names the
// provider, so the message does not repeat it. InternalsVisibleTo for the four provider assemblies is in
// GlobalUsings.cs.
internal static partial class EfCoreStorageLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug, Message = "Task {TaskType} persisted")]
    public static partial void TaskPersisted(this ILogger logger, string taskType);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Retrieving Pending Tasks (keyset: lastCreatedAt={LastCreatedAt}, lastId={LastId}, take={Take})")]
    public static partial void RetrievingPendingTasks(this ILogger logger, DateTimeOffset? lastCreatedAt,
                                                      Guid? lastId, int take);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Debug,
        Message = "Task {TaskId} is no longer recoverable, skipping SetQueued")]
    public static partial void TaskNoLongerRecoverable(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug, Message = "Set Task {TaskId} with Status {Status}")]
    public static partial void SettingTaskStatus(this ILogger logger, Guid taskId, QueuedTaskStatus status);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning,
        Message = "Task {TaskId} not found for status update to {Status}")]
    public static partial void TaskNotFoundForStatusUpdate(this ILogger logger, Guid taskId, QueuedTaskStatus status);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Critical,
        Message = "Unable to update the status {Status} for taskId {TaskId}")]
    public static partial void StatusUpdateFailed(this ILogger logger, Exception exception, QueuedTaskStatus status,
                                                  Guid taskId);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Debug,
        Message = "Get the current run counter for Task {TaskId}")]
    public static partial void GettingCurrentRunCount(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Debug,
        Message = "Update the current run counter for Task {TaskId}")]
    public static partial void UpdatingCurrentRun(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Warning,
        Message = "Task {TaskId} not found for run count update")]
    public static partial void TaskNotFoundForRunCountUpdate(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Critical,
        Message = "Unable to update the current run counter for taskId {TaskId}")]
    public static partial void CurrentRunUpdateFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Debug, Message = "Complete recurring run for Task {TaskId}")]
    public static partial void CompletingRecurringRun(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Warning,
        Message = "Task {TaskId} not found for recurring completion")]
    public static partial void TaskNotFoundForRecurringCompletion(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Critical,
        Message = "Unable to complete recurring run for taskId {TaskId}")]
    public static partial void RecurringRunCompletionFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Information,
        Message = "Finalize recurring series (terminal skip) for Task {TaskId}")]
    public static partial void FinalizingRecurringSeries(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Warning,
        Message = "Task {TaskId} not found for recurring series completion")]
    public static partial void TaskNotFoundForRecurringSeriesCompletion(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2015, Level = LogLevel.Critical,
        Message = "Unable to finalize recurring series for taskId {TaskId}")]
    public static partial void RecurringSeriesFinalizationFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 2016, Level = LogLevel.Information,
        Message = "Poison recurring Task {TaskId} terminally")]
    public static partial void PoisoningRecurringTask(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2017, Level = LogLevel.Warning,
        Message = "Task {TaskId} not found for recurring poison")]
    public static partial void TaskNotFoundForRecurringPoison(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2018, Level = LogLevel.Critical, Message = "Unable to poison recurring task {TaskId}")]
    public static partial void RecurringTaskPoisonFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 2019, Level = LogLevel.Debug, Message = "Updating task {TaskId} with key {TaskKey}")]
    public static partial void UpdatingTask(this ILogger logger, Guid taskId, string? taskKey);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Warning, Message = "Task {TaskId} not found for update")]
    public static partial void TaskNotFoundForUpdate(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2021, Level = LogLevel.Critical, Message = "Unable to update task {TaskId}")]
    public static partial void TaskUpdateFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 2022, Level = LogLevel.Debug, Message = "Removing task {TaskId}")]
    public static partial void RemovingTask(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2023, Level = LogLevel.Warning, Message = "Task {TaskId} not found for removal")]
    public static partial void TaskNotFoundForRemoval(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2024, Level = LogLevel.Critical, Message = "Unable to remove task {TaskId}")]
    public static partial void TaskRemoveFailed(this ILogger logger, Exception exception, Guid taskId);
}

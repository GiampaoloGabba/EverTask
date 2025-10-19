using System.Collections.Concurrent;

namespace EverTask.Handler;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the NotificationHandlerExecutor.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/NotificationHandlerExecutor.cs

public record TaskHandlerExecutor(
    IEverTask Task,
    object Handler,
    DateTimeOffset? ExecutionTime,
    RecurringTask? RecurringTask,
    Func<IEverTask, CancellationToken, Task> HandlerCallback,
    Func<Guid, Exception?, string, ValueTask>? HandlerErrorCallback,
    Func<Guid, ValueTask>? HandlerStartedCallback,
    Func<Guid, ValueTask>? HandlerCompletedCallback,
    Guid PersistenceId,
    string? QueueName,
    string? TaskKey);

public static class TaskHandlerExecutorExtensions
{
    // Performance optimization: Cache type metadata strings to avoid repeated generation
    private static readonly ConcurrentDictionary<Type, string> AssemblyQualifiedNameCache = new();
    private static readonly ConcurrentDictionary<RecurringTask, string> RecurringTaskToStringCache = new();

    public static QueuedTask ToQueuedTask(this TaskHandlerExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor.Task);
        ArgumentNullException.ThrowIfNull(executor.Handler);

        var request = JsonConvert.SerializeObject(executor.Task);

        // Use cached AssemblyQualifiedName to avoid repeated string generation
        var requestType = AssemblyQualifiedNameCache.GetOrAdd(
            executor.Task.GetType(),
            type => type.AssemblyQualifiedName ?? throw new InvalidOperationException($"Type {type} has no AssemblyQualifiedName"));

        var handlerType = AssemblyQualifiedNameCache.GetOrAdd(
            executor.Handler.GetType(),
            type => type.AssemblyQualifiedName ?? throw new InvalidOperationException($"Type {type} has no AssemblyQualifiedName"));

        bool            isRecurring      = false;
        string?         scheduleTask     = null;
        DateTimeOffset? nextRun          = null;
        string?         scheduleTaskInfo = null;
        int?            maxRuns          = null;
        DateTimeOffset? runUntil         = null;

        if (executor.RecurringTask != null)
        {
            scheduleTask = JsonConvert.SerializeObject(executor.RecurringTask);
            isRecurring = true;
            nextRun = executor.RecurringTask.CalculateNextRun(DateTimeOffset.UtcNow, 0);

            // Cache RecurringTask.ToString() result to avoid repeated string generation
            scheduleTaskInfo = RecurringTaskToStringCache.GetOrAdd(
                executor.RecurringTask,
                rt => rt.ToString() ?? "Recurring Task");

            maxRuns = executor.RecurringTask.MaxRuns;
            runUntil = executor.RecurringTask.RunUntil;
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(handlerType);

        return new QueuedTask
        {
            Id                    = executor.PersistenceId,
            Type                  = requestType,
            Request               = request,
            Handler               = handlerType,
            Status                = QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc          = DateTimeOffset.UtcNow,
            ScheduledExecutionUtc = executor.ExecutionTime,
            IsRecurring           = isRecurring,
            RecurringTask         = scheduleTask,
            RecurringInfo         = scheduleTaskInfo,
            MaxRuns               = maxRuns,
            RunUntil              = runUntil,
            NextRunUtc            = nextRun,
            CurrentRunCount       = 0,
            QueueName             = executor.QueueName,
            TaskKey               = executor.TaskKey
        };
    }
}

namespace EverTask.Monitoring;

public record EverTaskEventData(
    Guid TaskId,
    DateTimeOffset EventDateUtc,
    string Severity,
    string TaskType,
    string TaskHandlerType,
    string TaskParameters,
    string Message,
    string? Exception = null,
    IReadOnlyList<TaskExecutionLog>? ExecutionLogs = null)
{
    // Schedule/occurrence context lives in INIT properties, never as appended positional parameters:
    // appending would change the primary constructor and Deconstruct signatures of a record consumers
    // already build and deconstruct (X4). Subscribers compiled before these existed keep working.

    /// <summary>The recurring schedule this event's task is an occurrence of, when it is one.</summary>
    public Guid? ParentTaskId { get; init; }

    /// <summary>The nominal slot the reported delivery belongs to, when the task is scheduled.</summary>
    public DateTimeOffset? ScheduledAtUtc { get; init; }

    /// <summary>
    /// Version of the schedule definition behind this delivery: set on a recurring schedule row and on every
    /// occurrence of one, null for a task that belongs to no schedule.
    /// </summary>
    public int? ScheduleVersion { get; init; }

    internal static EverTaskEventData FromExecutor(TaskHandlerExecutor executor, SeverityLevel severity,
                                                   string message, Exception? exception,
                                                   IReadOnlyList<TaskExecutionLog>? executionLogs = null) =>
        FromExecutor(executor, severity.ToString(), executor.Task.GetType().ToString(),
            ResolveHandlerTypeName(executor, PlainTypeName), EverTaskJson.Serialize(executor.Task), message,
            exception, executionLogs);

    /// <summary>
    /// The ONE mapping from an executor to an event. The worker's hot path resolves the type strings and the
    /// task JSON through its own caches and passes them in; everything else goes through the overload above,
    /// which computes them plainly.
    /// </summary>
    /// <remarks>
    /// Both entry points must produce the same event, occurrence context included — a second copy of the
    /// mapping is a copy that drifts, and the one production actually publishes would be the one no test
    /// covers.
    /// </remarks>
    internal static EverTaskEventData FromExecutor(TaskHandlerExecutor executor, string severity, string taskType,
                                                   string handlerType, string taskParameters, string message,
                                                   Exception? exception,
                                                   IReadOnlyList<TaskExecutionLog>? executionLogs) =>
        new(executor.PersistenceId,
            DateTimeOffset.UtcNow,
            severity,
            taskType,
            handlerType,
            taskParameters,
            message,
            exception?.ToDetailedString(),
            executionLogs)
        {
            ParentTaskId    = executor.ParentTaskId,
            ScheduledAtUtc  = executor.NominalSlotOfDelivery,
            // A delivery belongs to a schedule either as the schedule row itself or as one of its
            // occurrences — and an occurrence never carries the definition: ApplyOccurrenceContract strips it
            // and leaves the parent id behind. Deriving the version from the definition alone therefore
            // dropped it on exactly the rows that report a schedule's real executions.
            ScheduleVersion = executor.RecurringTask != null || executor.ParentTaskId != null
                                  ? executor.ScheduleVersion
                                  : null
        };

    private static readonly Func<Type, string> PlainTypeName = static type => type.ToString();

    /// <summary>
    /// The handler's type name: from the eager instance when there is one, otherwise from the lazy
    /// assembly-qualified name. <paramref name="typeName"/> lets the worker route the eager branch through
    /// its permanent type-string cache.
    /// </summary>
    internal static string ResolveHandlerTypeName(TaskHandlerExecutor executor, Func<Type, string> typeName)
    {
        if (executor.Handler != null)
            return typeName(executor.Handler.GetType());

        // Lazy mode: extract simple type name from AssemblyQualifiedName
        return string.IsNullOrEmpty(executor.HandlerTypeName)
                   ? "Unknown"
                   : executor.HandlerTypeName.Split(',')[0].Trim();
    }
};

public enum SeverityLevel
{
    Information,
    Warning,
    Error
}

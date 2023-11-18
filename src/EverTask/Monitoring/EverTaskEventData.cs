namespace EverTask.Monitoring;

public record EverTaskEventData(
    Guid TaskId,
    DateTimeOffset EventDateUtc,
    SeverityLevel Severity,
    Type TaskType,
    Type TaskHandlerType,
    string TaskParameters,
    DateTimeOffset? TaskTaskScheduledExecutionUtc,
    string Message,
    Exception? Exception = null)
{
    internal static EverTaskEventData FromExecutor(TaskHandlerExecutor executor, SeverityLevel severity, string message, Exception? exception) =>
        new (
            executor.PersistenceId,
            DateTimeOffset.UtcNow,
            severity,
            executor.Task.GetType(),
            executor.Handler.GetType(),
            JsonConvert.SerializeObject(executor.Task),
            executor.ExecutionTime,
            message,
            exception);
};

public enum SeverityLevel
{
    Information,
    Warning,
    Error
}

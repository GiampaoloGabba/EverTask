namespace EverTask.Monitoring;

public record EverTaskEventData(
    Guid TaskId,
    DateTimeOffset EventDateUtc,
    string Severity,
    string TaskType,
    string TaskHandlerType,
    string TaskParameters,
    string Message,
    string? Exception = null)
{
    internal static EverTaskEventData FromExecutor(TaskHandlerExecutor executor, SeverityLevel severity, string message, Exception? exception) =>
        new (
            executor.PersistenceId,
            DateTimeOffset.UtcNow,
            severity.ToString(),
            executor.Task.GetType().ToString(),
            executor.Handler.GetType().ToString(),
            JsonConvert.SerializeObject(executor.Task),
            message,
            exception.ToDetailedString());
};

public enum SeverityLevel
{
    Information,
    Warning,
    Error
}

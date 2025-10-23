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
    internal static EverTaskEventData FromExecutor(TaskHandlerExecutor executor, SeverityLevel severity, string message, Exception? exception, IReadOnlyList<TaskExecutionLog>? executionLogs = null)
    {
        // Handler type: get from Handler instance (eager) or HandlerTypeName (lazy)
        string handlerType;
        if (executor.Handler != null)
        {
            handlerType = executor.Handler.GetType().ToString();
        }
        else if (!string.IsNullOrEmpty(executor.HandlerTypeName))
        {
            // Lazy mode: extract simple type name from AssemblyQualifiedName
            handlerType = executor.HandlerTypeName.Split(',')[0].Trim();
        }
        else
        {
            handlerType = "Unknown";
        }

        return new EverTaskEventData(
            executor.PersistenceId,
            DateTimeOffset.UtcNow,
            severity.ToString(),
            executor.Task.GetType().ToString(),
            handlerType,
            JsonConvert.SerializeObject(executor.Task),
            message,
            exception?.ToDetailedString(),
            executionLogs);
    }
};

public enum SeverityLevel
{
    Information,
    Warning,
    Error
}

namespace EverTask.Handler;

public record TaskHandlerExecutor(
    IEverTask Task,
    object Handler,
    Func<IEverTask, CancellationToken, Task> HandlerCallback,
    Func<Exception?, string, ValueTask>? HandlerErrorCallback,
    Func<ValueTask>? HandlerCompletedCallback,
    Guid PersistenceId);

public static class TaskHandlerExecutorExtensions
{
    public static QueuedTask ToQueuedTask(this TaskHandlerExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor.Task);
        ArgumentNullException.ThrowIfNull(executor.Handler);

        var request = JsonConvert.SerializeObject(executor.Task);
        var requestType = executor.Task.GetType().AssemblyQualifiedName;
        var handlerType = executor.Handler.GetType().AssemblyQualifiedName;

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(handlerType);

        return new QueuedTask
        {
            Id           = executor.PersistenceId,
            Type         = requestType,
            Request      = request,
            Handler      = handlerType,
            Status       = QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}

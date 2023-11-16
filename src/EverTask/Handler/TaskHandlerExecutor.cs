namespace EverTask.Handler;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the NotificationHandlerExecutor.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/NotificationHandlerExecutor.cs

public record TaskHandlerExecutor(
    IEverTask Task,
    object Handler,
    DateTimeOffset? ExecutionTime,
    Func<IEverTask, CancellationToken, Task> HandlerCallback,
    Func<Guid, Exception?, string, ValueTask>? HandlerErrorCallback,
    Func<Guid, ValueTask>? HandlerStartedCallback,
    Func<Guid, ValueTask>? HandlerCompletedCallback,
    Guid PersistenceId);

public static class TaskHandlerExecutorExtensions
{
    public static QueuedTask ToQueuedTask(this TaskHandlerExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor.Task);
        ArgumentNullException.ThrowIfNull(executor.Handler);

        var request       = JsonConvert.SerializeObject(executor.Task);
        var requestType   = executor.Task.GetType().AssemblyQualifiedName;
        var handlerType   = executor.Handler.GetType().AssemblyQualifiedName;

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
            ScheduledExecutionUtc = executor.ExecutionTime
        };
    }
}

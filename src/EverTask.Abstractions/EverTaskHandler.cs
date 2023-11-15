namespace EverTask.Abstractions;

public abstract class EverTaskHandler<T> : IEverTaskHandler<T> where T : IEverTask
{
    public abstract Task Handle(T backgroundTask, CancellationToken cancellationToken);

    public virtual ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask OnStarted(Guid persistenceId)
    {
        return default;
    }

    public virtual ValueTask OnCompleted(Guid persistenceId)
    {
        return ValueTask.CompletedTask;
    }
}

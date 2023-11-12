namespace EverTask.Abstractions;

public abstract class EverTaskHandler<T> : IEverTaskHandler<T> where T : IEverTask
{
    public abstract Task Handle(T backgroundTask, CancellationToken cancellationToken);

    public virtual ValueTask OnError(Exception? exception, string? message)
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask OnStorageError(Exception? exception, string? message)
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask Completed()
    {
        return ValueTask.CompletedTask;
    }
}

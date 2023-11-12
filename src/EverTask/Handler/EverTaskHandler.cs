namespace EverTask.Handler;

public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask
{
    public Func<(Exception? exception, string? message), Task>? ErrorHandler { get; set; }

    Task IEverTaskHandler<TTask>.Handle(TTask notification, CancellationToken cancellationToken)
    {
        Handle(notification, cancellationToken).ConfigureAwait(false);

        return Task.CompletedTask;
    }

    public abstract Task Handle(TTask backgroundTask, CancellationToken cancellationToken);

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

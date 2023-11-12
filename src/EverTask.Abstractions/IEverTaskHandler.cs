namespace EverTask.Abstractions;

/// <summary>
/// Defines a handler for a background task
/// </summary>
/// <typeparam name="TTask">The type of task being handled</typeparam>
public interface IEverTaskHandler<in TTask> where TTask : IEverTask
{
    Task Handle(TTask backgroundTask, CancellationToken cancellationToken);
    ValueTask OnError(Exception? exception, string? message);
    ValueTask OnStorageError(Exception? exception, string? message);
    ValueTask Completed();
}

namespace EverTask.Worker;

public interface IWorkerQueue
{
    ValueTask Queue(TaskHandlerExecutor task);
    Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken);
    IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken);
}

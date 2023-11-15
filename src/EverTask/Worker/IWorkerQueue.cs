namespace EverTask.Worker;

public interface IWorkerQueue
{
    Task Queue(TaskHandlerExecutor task);
    Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken);
    IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken);
}

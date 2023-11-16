namespace EverTask.Scheduler;

public interface IDelayedQueue
{
    void Enqueue(TaskHandlerExecutor item);
}

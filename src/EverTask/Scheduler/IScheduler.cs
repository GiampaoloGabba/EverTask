namespace EverTask.Scheduler;

public interface IScheduler
{
    void Schedule(TaskHandlerExecutor item);
}

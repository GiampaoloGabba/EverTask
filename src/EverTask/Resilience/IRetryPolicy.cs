namespace EverTask.Resilience;

public interface IRetryPolicy
{
    Task Execute(Func<Task> action);
}

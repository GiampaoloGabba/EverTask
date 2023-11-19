namespace EverTask.Abstractions;

public interface IRetryPolicy
{
    Task Execute(Func<CancellationToken, Task> action, CancellationToken token = default);
}

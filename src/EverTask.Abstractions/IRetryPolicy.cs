using Microsoft.Extensions.Logging;

namespace EverTask.Abstractions;

public interface IRetryPolicy
{
    Task Execute(Func<CancellationToken, Task> action, ILogger attemptLogger, CancellationToken token = default);
}

using EverTask.Abstractions;

namespace EverTask.Example.AspnetCore;

/// <summary>
/// Quick task for simple one-shot operations with minimal logging
/// </summary>
public record QuickTask(
    string Operation,
    int DurationMs = 500
) : IEverTask;

public class QuickTaskHandler : EverTaskHandler<QuickTask>
{
    private readonly ILogger<QuickTaskHandler> _logger;

    public QuickTaskHandler(ILogger<QuickTaskHandler> logger)
    {
        _logger = logger;
    }

    public override async Task Handle(QuickTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing quick operation: {Operation}", task.Operation);

        await Task.Delay(task.DurationMs, cancellationToken);

        _logger.LogInformation("Operation '{Operation}' completed in {Duration}ms",
            task.Operation, task.DurationMs);
    }
}

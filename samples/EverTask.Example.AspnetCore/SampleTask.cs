using EverTask.Abstractions;

namespace EverTask.Example.AspnetCore;

public record SampleTaskRequest(string TestProperty) : IEverTask;

public class SampleTaskRequestHanlder : EverTaskHandler<SampleTaskRequest>
{
    private readonly ILogger<SampleTaskRequestHanlder> _logger;

    public SampleTaskRequestHanlder(ILogger<SampleTaskRequestHanlder> logger)
    {
        _logger = logger;
    }
    public override Task Handle(SampleTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        _logger.LogInformation(backgroundTask.TestProperty);
        return Task.CompletedTask;
    }

    public override ValueTask OnStarted(Guid persistenceId)
    {
        _logger.LogInformation("====== TASK WITH ID {persistenceId} STARTED IN BACKGROUND ======", persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid persistenceId)
    {
        _logger.LogInformation("====== TASK WITH ID {persistenceId} COMPLETED IN BACKGROUND ======", persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        _logger.LogInformation("====== TASK WITH ID {persistenceId} FAILED ======", persistenceId);
        _logger.LogError(exception, message);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _logger.LogInformation("====== TASK DISPOSED IN BACKGROUND ======");
        return base.DisposeAsyncCore();
    }
}

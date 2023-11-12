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

    public override ValueTask Completed()
    {
        return base.Completed();
    }

    public override ValueTask OnError(Exception? exception, string? message)
    {
        return base.OnError(exception, message);
    }
}

using EverTask.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverTask.Example.Console;

public record SampleTaskRequest(string TestProperty) : IEverTask;

public class SampleTaskRequestHanlder(ILogger<SampleTaskRequestHanlder> logger) : EverTaskHandler<SampleTaskRequest>
{
    public override Task Handle(SampleTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        logger.LogInformation(backgroundTask.TestProperty);
        return Task.CompletedTask;
    }

    public override ValueTask OnStarted(Guid persistenceId)
    {
        logger.LogInformation("**** STARTED: {persistenceId} at {date}", persistenceId, DateTimeOffset.Now);
        return ValueTask.CompletedTask;
    }
}

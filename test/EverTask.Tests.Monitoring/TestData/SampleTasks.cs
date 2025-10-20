namespace EverTask.Tests.Monitoring.TestData;

/// <summary>
/// Sample task for testing
/// </summary>
public record SampleTask(string Message) : IEverTask;

/// <summary>
/// Sample task handler
/// </summary>
public class SampleTaskHandler : EverTaskHandler<SampleTask>
{
    public override async Task Handle(SampleTask task, CancellationToken ct)
    {
        // Simulate some work
        await Task.Delay(100, ct);
    }
}

/// <summary>
/// Sample recurring task
/// </summary>
public record SampleRecurringTask(string Message) : IEverTask;

/// <summary>
/// Sample recurring task handler
/// </summary>
public class SampleRecurringTaskHandler : EverTaskHandler<SampleRecurringTask>
{
    public override async Task Handle(SampleRecurringTask task, CancellationToken ct)
    {
        await Task.Delay(50, ct);
    }
}

/// <summary>
/// Sample task that fails
/// </summary>
public record SampleFailingTask(string Message) : IEverTask;

/// <summary>
/// Sample failing task handler
/// </summary>
public class SampleFailingTaskHandler : EverTaskHandler<SampleFailingTask>
{
    public override Task Handle(SampleFailingTask task, CancellationToken ct)
    {
        throw new InvalidOperationException("This task always fails");
    }
}

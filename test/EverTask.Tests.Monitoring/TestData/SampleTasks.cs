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

/// <summary>
/// A task whose handler takes visibly longer than the tolerance a badge cares about, so the moment a run
/// STARTS and the moment it ENDS cannot be mistaken for each other.
/// </summary>
public record SlowSampleTask(string Message) : IEverTask;

/// <summary>Handler of <see cref="SlowSampleTask"/>.</summary>
public class SlowSampleTaskHandler : EverTaskHandler<SlowSampleTask>
{
    /// <summary>How long one run of this handler takes.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(2.5);

    public override Task Handle(SlowSampleTask task, CancellationToken ct) => Task.Delay(Duration, ct);
}

/// <summary>
/// A task that runs for a while and THEN fails: the shape that leaves a row with an end, no measured
/// duration, and a start that only the audit trail knows.
/// </summary>
public record SlowFailingSampleTask(string Message) : IEverTask;

/// <summary>Handler of <see cref="SlowFailingSampleTask"/>.</summary>
public class SlowFailingSampleTaskHandler : EverTaskHandler<SlowFailingSampleTask>
{
    /// <summary>How long one run of this handler takes before it throws.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(2.5);

    public override async Task Handle(SlowFailingSampleTask task, CancellationToken ct)
    {
        await Task.Delay(Duration, ct);
        throw new InvalidOperationException("this task always fails, after taking its time");
    }
}

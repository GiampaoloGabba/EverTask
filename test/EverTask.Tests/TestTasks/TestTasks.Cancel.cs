using System.Collections.Concurrent;

namespace EverTask.Tests;

// Test tasks and shared state for the M4 cancel-pipeline scenarios.

public sealed class CancelTestState
{
    public ConcurrentBag<int> Executed { get; } = new();

    /// <summary>Released by a handler when it starts executing.</summary>
    public SemaphoreSlim Entered { get; } = new(0, int.MaxValue);

    /// <summary>Handlers wait on this gate until the test releases it.</summary>
    public SemaphoreSlim Gate { get; } = new(0, int.MaxValue);
}

/// <summary>Blocks observing the cancellation token (default queue).</summary>
public record CancelBlockingTask : IEverTask;

public class CancelBlockingTaskHandler(CancelTestState state) : EverTaskHandler<CancelBlockingTask>
{
    public override async Task Handle(CancelBlockingTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Entered.Release();
        await state.Gate.WaitAsync(cancellationToken);
    }
}

/// <summary>Recurring task that blocks its first occurrence observing the token.</summary>
public record CancelRecurringBlockingTask : IEverTask;

public class CancelRecurringBlockingTaskHandler(CancelTestState state) : EverTaskHandler<CancelRecurringBlockingTask>
{
    public override async Task Handle(CancelRecurringBlockingTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Executed.Add(1);
        state.Entered.Release();
        await state.Gate.WaitAsync(cancellationToken);
    }
}

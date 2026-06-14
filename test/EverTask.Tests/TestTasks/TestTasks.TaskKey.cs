using System.Collections.Concurrent;

namespace EverTask.Tests;

// Test tasks and shared state for the M3 taskKey dedup / in-flight payload scenarios.

public sealed class TaskKeyTestState
{
    /// <summary>Payloads of executed <see cref="TaskKeyPayloadTask"/> (and -1 for recurring runs).</summary>
    public ConcurrentBag<int> ExecutedPayloads { get; } = new();

    /// <summary>Released by the blocking handler when it starts executing.</summary>
    public SemaphoreSlim BlockingEntered { get; } = new(0, int.MaxValue);

    /// <summary>The blocking handler waits on this gate until the test releases it.</summary>
    public SemaphoreSlim BlockingGate { get; } = new(0, int.MaxValue);
}

/// <summary>Carries an integer payload (default queue) so the executed value can be compared to storage.</summary>
public record TaskKeyPayloadTask(int Payload) : IEverTask;

public class TaskKeyPayloadTaskHandler(TaskKeyTestState state) : EverTaskHandler<TaskKeyPayloadTask>
{
    public override Task Handle(TaskKeyPayloadTask backgroundTask, CancellationToken cancellationToken)
    {
        state.ExecutedPayloads.Add(backgroundTask.Payload);
        return Task.CompletedTask;
    }
}

/// <summary>Blocking task on the DEFAULT queue: occupies the single consumer so another dispatch
/// stays queued (delivering) without being dequeued.</summary>
public record TaskKeyBlockerTask : IEverTask;

public class TaskKeyBlockerTaskHandler(TaskKeyTestState state) : EverTaskHandler<TaskKeyBlockerTask>
{
    public override async Task Handle(TaskKeyBlockerTask backgroundTask, CancellationToken cancellationToken)
    {
        state.BlockingEntered.Release();
        await state.BlockingGate.WaitAsync(cancellationToken);
    }
}

/// <summary>Recurring task (for the recurring-vs-one-shot taskKey test).</summary>
public record TaskKeyRecurringTask : IEverTask;

public class TaskKeyRecurringTaskHandler(TaskKeyTestState state) : EverTaskHandler<TaskKeyRecurringTask>
{
    public override Task Handle(TaskKeyRecurringTask backgroundTask, CancellationToken cancellationToken)
    {
        state.ExecutedPayloads.Add(-1);
        return Task.CompletedTask;
    }
}

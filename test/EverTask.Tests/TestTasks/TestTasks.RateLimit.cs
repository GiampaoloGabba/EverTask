using System.Collections.Concurrent;

namespace EverTask.Tests;

// Test tasks and shared state for keyed rate limiting scenarios.
// Modeled on TestTasks.Resilience.cs: register a fresh RateLimitTestState per test host.

/// <summary>
/// Shared, test-local coordination state for rate limiting tests.
/// </summary>
public class RateLimitTestState
{
    /// <summary>Executions recorded by rate-limited handlers (key, index, wall-clock UTC).</summary>
    public ConcurrentBag<(string Key, int Index, DateTimeOffset ExecutedAtUtc)> Executions { get; } = new();

    /// <summary>Execution count per task index (exactly-once assertions).</summary>
    public ConcurrentDictionary<int, int> ExecutionCountByIndex { get; } = new();

    /// <summary>Payloads executed by <see cref="RateLimitedPayloadTask"/> handlers.</summary>
    public ConcurrentBag<string> ExecutedPayloads { get; } = new();

    /// <summary>Signaled when a <see cref="RateLimitedSlowTask"/> handler starts executing.</summary>
    public SemaphoreSlim SlowEntered { get; } = new(0, int.MaxValue);

    /// <summary>Slow handlers wait on this gate until the test releases them.</summary>
    public SemaphoreSlim SlowGate { get; } = new(0, int.MaxValue);

    /// <summary>Execution count of <see cref="RateLimitedSlowTask"/> handlers.</summary>
    public int SlowExecutions;

    public void Record(string key, int index)
    {
        Executions.Add((key, index, DateTimeOffset.UtcNow));
        ExecutionCountByIndex.AddOrUpdate(index, 1, static (_, count) => count + 1);
    }

    /// <summary>Ordered execution timestamps for a key.</summary>
    public DateTimeOffset[] TimestampsForKey(string key) =>
        Executions.Where(e => e.Key == key).Select(e => e.ExecutedAtUtc).OrderBy(t => t).ToArray();
}

/// <summary>One permit per 900 ms per key (no burst): the general single-permit type.</summary>
public record RateLimitedSinglePermitTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedSinglePermitTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedSinglePermitTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(900)) { Burst = 1 };

    public override Task Handle(RateLimitedSinglePermitTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>One permit per 8 s per key: long-window type for restart scenarios (the parked slot must not fire during host shutdown).</summary>
public record RateLimitedLongWindowTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedLongWindowTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedLongWindowTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(8)) { Burst = 1 };

    public override Task Handle(RateLimitedLongWindowTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>One permit per 2 s per key: medium-window type for cancel-while-parked scenarios.</summary>
public record RateLimitedMediumWindowTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedMediumWindowTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedMediumWindowTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(2)) { Burst = 1 };

    public override Task Handle(RateLimitedMediumWindowTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>Carries a payload: used for same-taskKey re-dispatch (latest payload wins) scenarios.</summary>
public record RateLimitedPayloadTask(string Key, string Payload) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedPayloadTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedPayloadTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(2)) { Burst = 1 };

    public override Task Handle(RateLimitedPayloadTask backgroundTask, CancellationToken cancellationToken)
    {
        state.ExecutedPayloads.Add(backgroundTask.Payload);
        return Task.CompletedTask;
    }
}

/// <summary>Two permits per 700 ms per key (burst 2): the flood type.</summary>
public record RateLimitedFloodTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedFloodTaskHandler(RateLimitTestState state) : EverTaskHandler<RateLimitedFloodTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(2, TimeSpan.FromMilliseconds(700)) { Burst = 2 };

    public override Task Handle(RateLimitedFloodTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Routed to the "tiny" queue; the rate-limit gate is per task type, so a FallbackToDefault
/// reroute to the default queue must still be gated.
/// </summary>
public record RateLimitedQueueRoutedTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedQueueRoutedTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedQueueRoutedTask>
{
    public override string? QueueName => "tiny";

    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(1500)) { Burst = 1 };

    public override async Task Handle(RateLimitedQueueRoutedTask backgroundTask, CancellationToken cancellationToken)
    {
        if (backgroundTask.Index == 0)
        {
            // The first task blocks its consumer so the tiny queue saturates
            state.SlowEntered.Release();
            await state.SlowGate.WaitAsync(cancellationToken);
        }

        state.Record(backgroundTask.Key, backgroundTask.Index);
    }
}

/// <summary>Stateless type for wrapper extraction unit tests (policy + key stamping).</summary>
public record RateLimitedWrapperTask(string Tenant) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Tenant;
}

public class RateLimitedWrapperTaskHandler : EverTaskHandler<RateLimitedWrapperTask>
{
    public static readonly RateLimitPolicy DeclaredPolicy = new(5, TimeSpan.FromMinutes(1));

    public override RateLimitPolicy? RateLimitPolicy => DeclaredPolicy;

    public override Task Handle(RateLimitedWrapperTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>Type whose key selector throws: the dispatch must proceed ungated (fail-safe).</summary>
public record RateLimitedThrowingKeyTask : IEverTask;

public class RateLimitedThrowingKeyTaskHandler : EverTaskHandler<RateLimitedThrowingKeyTask>
{
    public override RateLimitPolicy? RateLimitPolicy => new(5, TimeSpan.FromMinutes(1));

    public override string? GetRateLimitKey(RateLimitedThrowingKeyTask task) =>
        throw new InvalidOperationException("broken key selector");

    public override Task Handle(RateLimitedThrowingKeyTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>One permit per 700 ms; the handler blocks until released (in-flight duplicate scenarios).</summary>
public record RateLimitedSlowTask(string Key) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedSlowTaskHandler(RateLimitTestState state) : EverTaskHandler<RateLimitedSlowTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(700)) { Burst = 1 };

    public override async Task Handle(RateLimitedSlowTask backgroundTask, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref state.SlowExecutions);
        state.SlowEntered.Release();
        await state.SlowGate.WaitAsync(cancellationToken);
    }
}

using System.Collections.Concurrent;
using EverTask.Resilience;

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

    /// <summary>OnError callbacks received by rate-limited handlers (index, exception).</summary>
    public ConcurrentBag<(int Index, Exception? Exception)> OnErrors { get; } = new();

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

/// <summary>
/// Throttled retries (default ThrottleRetries=true): fails until the 3rd attempt; each attempt
/// records a timestamp, so the test can assert the rate-limit spacing between retries.
/// </summary>
public record RateLimitedRetryTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedRetryTaskHandler(RateLimitTestState state) : EverTaskHandler<RateLimitedRetryTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(700)) { Burst = 1 }; // ThrottleRetries defaults to true

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(4, TimeSpan.FromMilliseconds(30));

    public override Task Handle(RateLimitedRetryTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        if (state.ExecutionCountByIndex[backgroundTask.Index] < 3)
            throw new InvalidOperationException("transient failure (test)");

        return Task.CompletedTask;
    }
}

/// <summary>ThrottleRetries=false on a huge 60 s window: retries must bypass the limiter entirely.</summary>
public record RateLimitedUnthrottledRetryTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedUnthrottledRetryTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedUnthrottledRetryTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(60)) { Burst = 1, ThrottleRetries = false };

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(4, TimeSpan.FromMilliseconds(30));

    public override Task Handle(RateLimitedUnthrottledRetryTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        if (state.ExecutionCountByIndex[backgroundTask.Index] < 3)
            throw new InvalidOperationException("transient failure (test)");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Per-attempt 500 ms timeout with a ~600 ms budget wait between attempts: if the budget wait
/// eroded the timeout, the 300 ms second attempt could never succeed.
/// </summary>
public record RateLimitedTimeoutRetryTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedTimeoutRetryTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedTimeoutRetryTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(700)) { Burst = 1 };

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(30));

    public override TimeSpan? Timeout => TimeSpan.FromMilliseconds(500);

    public override async Task Handle(RateLimitedTimeoutRetryTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        if (state.ExecutionCountByIndex[backgroundTask.Index] == 1)
            throw new InvalidOperationException("transient failure (test)");

        // Second attempt: well within the 500 ms per-attempt timeout — unless the timeout was
        // eroded by the ~600 ms budget wait that preceded this attempt
        await Task.Delay(300, cancellationToken);
    }
}

/// <summary>
/// MaxInSlotWait=0 with a 2 s window: a throttled retry can never wait in-slot and must take the
/// re-park path (attempt count restarts on redelivery).
/// </summary>
public record RateLimitedReparkRetryTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedReparkRetryTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedReparkRetryTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(2)) { Burst = 1, MaxInSlotWait = TimeSpan.Zero };

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(30));

    public override Task Handle(RateLimitedReparkRetryTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        if (state.ExecutionCountByIndex[backgroundTask.Index] == 1)
            throw new InvalidOperationException("transient failure (test)");

        return Task.CompletedTask;
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

/// <summary>StartEmpty: 2 permits per 2 s, but a fresh bucket admits at the steady 1 s rate (no burst).</summary>
public record RateLimitedStartEmptyTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedStartEmptyTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedStartEmptyTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(2, TimeSpan.FromSeconds(2)) { Burst = 2, StartEmpty = true };

    public override Task Handle(RateLimitedStartEmptyTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>One permit per 5 s with a 1 s reservation horizon: the second task is terminally rejected.</summary>
public record RateLimitedHorizonTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedHorizonTaskHandler(RateLimitTestState state) : EverTaskHandler<RateLimitedHorizonTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(5)) { Burst = 1, MaxReservationHorizon = TimeSpan.FromSeconds(1) };

    public override Task Handle(RateLimitedHorizonTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        return Task.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        state.OnErrors.Add((-1, exception));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Recurring-friendly horizon type: 1 permit per 3.5 s, 1 s horizon — occurrences arriving while
/// the budget is exhausted are skipped (series alive) unless their slot is near enough to wait.
/// </summary>
public record RateLimitedHorizonRecurringTask(string Key) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedHorizonRecurringTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedHorizonRecurringTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(3500)) { Burst = 1, MaxReservationHorizon = TimeSpan.FromSeconds(1) };

    public override Task Handle(RateLimitedHorizonRecurringTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, 0);
        return Task.CompletedTask;
    }
}

/// <summary>Discard overflow: 1 permit per 5 s; an over-budget task fails immediately (no parking).</summary>
public record RateLimitedDiscardTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedDiscardTaskHandler(RateLimitTestState state) : EverTaskHandler<RateLimitedDiscardTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(5))
        {
            Burst            = 1,
            OverflowBehavior = RateLimitOverflowBehavior.Discard
        };

    public override Task Handle(RateLimitedDiscardTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, backgroundTask.Index);
        return Task.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        state.OnErrors.Add((-2, exception));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Recurring rhythm type: every-second series with a 700 ms refill and a tiny MaxInSlotWait so
/// throttled occurrences take the re-park path (rhythm must be preserved by QueueNextOccourrence).
/// </summary>
public record RateLimitedRecurringRhythmTask(string Key) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class RateLimitedRecurringRhythmTaskHandler(RateLimitTestState state)
    : EverTaskHandler<RateLimitedRecurringRhythmTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(700)) { Burst = 1, MaxInSlotWait = TimeSpan.FromMilliseconds(50) };

    public override Task Handle(RateLimitedRecurringRhythmTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Record(backgroundTask.Key, 0);
        return Task.CompletedTask;
    }
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

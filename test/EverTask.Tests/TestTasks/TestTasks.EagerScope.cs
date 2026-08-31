using System.Collections.Concurrent;

namespace EverTask.Tests;

// Tasks and state for the eager-handler-scope release tests: every handler here depends on a SCOPED
// probe, so the lifetime of the scope each delivery was resolved into is observable from outside.
// Register one state per test host.

/// <summary>
/// Shared, test-local state for the eager-scope tests.
/// </summary>
public class EagerScopeTestState
{
    /// <summary>Scoped probes built — one per scope that resolved a handler.</summary>
    public int ProbesCreated;

    /// <summary>Scoped probes disposed: a stranded scope never increments this.</summary>
    public int ProbesDisposed;

    /// <summary>Calls a handler made on its scoped probe.</summary>
    public int ProbesUsed;

    /// <summary>Indexes of the tasks whose handler really ran.</summary>
    public ConcurrentBag<int> Executed { get; } = new();

    /// <summary>Signaled when a blocking handler enters <c>Handle</c>.</summary>
    public SemaphoreSlim Entered { get; } = new(0, int.MaxValue);

    /// <summary>Blocking handlers wait here until the test releases them.</summary>
    public SemaphoreSlim Gate { get; } = new(0, int.MaxValue);

    /// <summary>
    /// Every hand-off to the scheduler, stamped with how many scopes had already been released by then.
    /// An ORDERED release site is one that runs before the delivery schedules whatever comes next, and a
    /// count taken inside the real scheduler is how that order is observed without stubbing anything out.
    /// </summary>
    public ConcurrentQueue<(Guid TaskId, int DisposedSoFar)> Scheduled { get; } = new();

    public int Created => Volatile.Read(ref ProbesCreated);

    public int Disposed => Volatile.Read(ref ProbesDisposed);

    public int Used => Volatile.Read(ref ProbesUsed);

    public void RecordScheduled(Guid taskId) => Scheduled.Enqueue((taskId, Disposed));

    public (Guid TaskId, int DisposedSoFar)[] ScheduledFor(Guid taskId) =>
        Scheduled.Where(entry => entry.TaskId == taskId).ToArray();
}

/// <summary>
/// A SCOPED dependency of a handler, standing in for the scoped DbContext of a real application: it is
/// built inside whatever scope resolves the handler and released only when that scope is disposed. In
/// eager mode that scope is the EverTask-owned one carried on the executor, so this probe's disposal is
/// the proof the scope itself was released instead of stranded.
/// </summary>
public sealed class EagerScopeProbe : IAsyncDisposable
{
    private readonly EagerScopeTestState _state;

    // Counting the construction is the whole point, so this one keeps a real constructor body.
    public EagerScopeProbe(EagerScopeTestState state)
    {
        _state = state;
        Interlocked.Increment(ref state.ProbesCreated);
    }

    /// <summary>A real use of the dependency: one nobody touches would prove nothing about its lifetime.</summary>
    public void Ping() => Interlocked.Increment(ref _state.ProbesUsed);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _state.ProbesDisposed);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// No rate-limit policy: the handler blocks until the test releases the gate, so a second delivery of the
/// same persistence id meets the in-flight guard and the consumer of a single-parallelism queue is held.
/// </summary>
public record EagerScopeBlockingTask(int Index) : IEverTask;

public class EagerScopeBlockingTaskHandler(EagerScopeTestState state, EagerScopeProbe probe)
    : EverTaskHandler<EagerScopeBlockingTask>
{
    public override async Task Handle(EagerScopeBlockingTask backgroundTask, CancellationToken cancellationToken)
    {
        probe.Ping();
        state.Executed.Add(backgroundTask.Index);
        state.Entered.Release();
        await state.Gate.WaitAsync(cancellationToken);
    }
}

/// <summary>One permit per 2 s per key: the deferral scenario (the second dispatch finds no budget).</summary>
public record EagerScopeGatedTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class EagerScopeGatedTaskHandler(EagerScopeTestState state, EagerScopeProbe probe)
    : EverTaskHandler<EagerScopeGatedTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(2)) { Burst = 1 };

    public override Task Handle(EagerScopeGatedTask backgroundTask, CancellationToken cancellationToken)
    {
        probe.Ping();
        state.Executed.Add(backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>
/// One permit per 30 s with <c>Discard</c>: once the warm-up has taken it, the next delivery on the key is
/// TERMINALLY rejected. Dispatched as a recurring series, that rejection takes the branch that skips the
/// occurrence and schedules the next one — the release site whose ORDER matters.
/// </summary>
public record EagerScopeRejectedTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class EagerScopeRejectedTaskHandler(EagerScopeTestState state, EagerScopeProbe probe)
    : EverTaskHandler<EagerScopeRejectedTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(30))
        {
            Burst            = 1,
            OverflowBehavior = RateLimitOverflowBehavior.Discard
        };

    public override Task Handle(EagerScopeRejectedTask backgroundTask, CancellationToken cancellationToken)
    {
        probe.Ping();
        state.Executed.Add(backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Rate limited AND blocking: a redelivery landing while this one is in flight is re-parked by the gate
/// before any budget is touched.
/// </summary>
public record EagerScopeGatedBlockingTask(string Key, int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => Key;
}

public class EagerScopeGatedBlockingTaskHandler(EagerScopeTestState state, EagerScopeProbe probe)
    : EverTaskHandler<EagerScopeGatedBlockingTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromMilliseconds(700)) { Burst = 1 };

    public override async Task Handle(EagerScopeGatedBlockingTask backgroundTask, CancellationToken cancellationToken)
    {
        probe.Ping();
        state.Executed.Add(backgroundTask.Index);
        state.Entered.Release();
        await state.Gate.WaitAsync(cancellationToken);
    }
}

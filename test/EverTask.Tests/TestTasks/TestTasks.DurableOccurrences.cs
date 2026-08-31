using System.Collections.Concurrent;

namespace EverTask.Tests;

// Tasks and shared state for the durable-occurrence suite. Registered per test host, like every other
// recorder here, so nothing leaks between tests running in parallel.

/// <summary>One execution, as the handler read it from its own <see cref="ITaskExecutionContext"/>.</summary>
public sealed record OccurrenceExecution(
    Guid TaskId,
    Guid? ScheduleId,
    DateTimeOffset? SlotUtc,
    int RunNumber,
    bool IsOccurrence,
    MisfireInfo? Misfire);

/// <summary>Test-local coordination state for the durable-occurrence handlers.</summary>
public sealed class DurableOccurrenceRecorder
{
    private int _live;
    private int _maxConcurrent;

    public ConcurrentQueue<OccurrenceExecution> Executions { get; } = new();

    /// <summary>How long each handler holds its slot, to make overlap observable.</summary>
    public TimeSpan Hold { get; set; } = TimeSpan.Zero;

    /// <summary>Run numbers whose handler always throws, so the occurrence ends Failed after its retries.</summary>
    public HashSet<int> FailRunNumbers { get; } = [];

    /// <summary>The highest number of handlers that were inside <c>Handle</c> at the same moment.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public int Count => Executions.Count;

    public OccurrenceExecution[] Snapshot() => Executions.ToArray();

    public async Task RecordAsync(ITaskExecutionContext context, CancellationToken ct)
    {
        var live = Interlocked.Increment(ref _live);
        RaiseMax(live);

        try
        {
            Executions.Enqueue(new OccurrenceExecution(context.TaskId, context.ScheduleId, context.ScheduledAtUtc,
                context.RunNumber, context.IsOccurrence, context.Misfire));

            if (Hold > TimeSpan.Zero)
                await Task.Delay(Hold, ct);

            if (FailRunNumbers.Contains(context.RunNumber))
                throw new InvalidOperationException($"run {context.RunNumber} fails on purpose");
        }
        finally
        {
            Interlocked.Decrement(ref _live);
        }
    }

    private void RaiseMax(int live)
    {
        int observed;
        while ((observed = Volatile.Read(ref _maxConcurrent)) < live)
        {
            if (Interlocked.CompareExchange(ref _maxConcurrent, live, observed) == observed)
                return;
        }
    }
}

/// <summary>The payload every durable-occurrence test schedules.</summary>
public record DurableProbeTask(string Marker) : IEverTask;

public class DurableProbeTaskHandler(DurableOccurrenceRecorder recorder) : EverTaskHandler<DurableProbeTask>
{
    // One quick retry instead of the global three at half a second: a test that makes an occurrence fail
    // wants it Failed, not three seconds of backoff first.
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(DurableProbeTask backgroundTask, CancellationToken cancellationToken) =>
        recorder.RecordAsync(Context, cancellationToken);
}

/// <summary>
/// A payload with NO handler anywhere, deliberately: the shape a re-registration leaves behind when a schedule
/// is pointed at another task and the old handler is deleted from the application. Its rows still deserialize,
/// which is what makes them look usable right up to the moment something tries to resolve a handler for them.
/// </summary>
public record HandlerlessOccurrenceTask(string Marker) : IEverTask;

/// <summary>
/// Holds a delivery inside DI resolution, which is the one stretch of a delivery no cancellation check covers:
/// the blacklist is read before the rate-limit gate, and the handler is resolved after it.
/// </summary>
public sealed class ResolutionGate
{
    private readonly TaskCompletionSource _entered  = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _armed;
    private int _handled;

    /// <summary>Completes once a resolution is really blocked, so a test never races the delivery it holds.</summary>
    public Task Entered => _entered.Task;

    /// <summary>How many times the held handler reached <c>Handle</c>.</summary>
    public int Handled => Volatile.Read(ref _handled);

    /// <summary>
    /// Holds the NEXT resolution and only that one. Building an executor already resolves the handler once —
    /// the dispatch path needs its per-type metadata — so a gate that held every resolution would hang the
    /// test that arms it instead of the delivery it means to hold.
    /// </summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>Called from the handler's CONSTRUCTOR: announces the stall and blocks until released.</summary>
    public void Hold()
    {
        if (Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
            return;

        _entered.TrySetResult();
        _released.Task.GetAwaiter().GetResult();
    }

    public void Release() => _released.TrySetResult();

    public void MarkHandled() => Interlocked.Increment(ref _handled);
}

/// <summary>A payload whose handler takes as long to resolve as the test wants it to.</summary>
public record SlowResolutionTask(string Marker) : IEverTask;

public sealed class SlowResolutionTaskHandler : EverTaskHandler<SlowResolutionTask>
{
    private readonly ResolutionGate _gate;

    // A traditional constructor, because the blocking IS the point: resolving this handler is what a slow
    // dependency (a DbContext opening a connection, a remote configuration read) costs a real delivery.
    public SlowResolutionTaskHandler(ResolutionGate gate)
    {
        _gate = gate;
        gate.Hold();
    }

    public override Task Handle(SlowResolutionTask backgroundTask, CancellationToken cancellationToken)
    {
        _gate.MarkHandled();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fails the activation of <see cref="FlakyResolutionTaskHandler"/> a set number of times and then lets it
/// through: a scoped dependency that is not there for a moment, which from the outside looks exactly like a
/// handler that is not registered at all — and must not be treated as one.
/// </summary>
public sealed class ActivationFaultGate
{
    private int _remaining;
    private int _activations;

    /// <summary>How many times the handler was really built (or tried to be).</summary>
    public int Activations => Volatile.Read(ref _activations);

    /// <summary>Makes the next <paramref name="times"/> activations throw.</summary>
    public void FailNext(int times) => Volatile.Write(ref _remaining, times);

    /// <summary>
    /// Makes every activation throw until <see cref="Release"/> is called — the dependency that is not coming
    /// back on its own, rather than the one that is away for a moment.
    /// </summary>
    /// <remarks>
    /// Counting the activations of a whole run instead would pin how many times the reconciliation happens to
    /// build a handler (the rebuild, then the probe that asks whether anything is registered at all), which is
    /// not what a test about the number of RUNS is saying.
    /// </remarks>
    public void FailUntilReleased() => Volatile.Write(ref _remaining, int.MaxValue);

    /// <summary>Lets activations through again.</summary>
    public void Release() => Volatile.Write(ref _remaining, 0);

    /// <summary>Called from the handler's CONSTRUCTOR, which is where a dependency is built.</summary>
    public void Enter()
    {
        Interlocked.Increment(ref _activations);

        if (Interlocked.Decrement(ref _remaining) >= 0)
            throw new TimeoutException("the dependency this handler needs is not there right now");
    }
}

/// <summary>A payload whose handler needs a dependency that is not always there.</summary>
public record FlakyResolutionTask(string Marker) : IEverTask;

public sealed class FlakyResolutionTaskHandler : EverTaskHandler<FlakyResolutionTask>
{
    // A traditional constructor, because what is under test is a resolution that FAILS: that is what a scoped
    // dependency whose factory throws costs a real delivery, and it happens while the handler is built.
    public FlakyResolutionTaskHandler(ActivationFaultGate gate) => gate.Enter();

    public override Task Handle(FlakyResolutionTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// The same probe routed to a queue of its own, for the invariant that an occurrence inherits the queue its
/// SCHEDULE was routed to: a child is dispatched with no recurring definition, and the fallback for one of
/// those is the default queue (M4).
/// </summary>
public record CustomQueueDurableTask(string Marker) : IEverTask;

public class CustomQueueDurableTaskHandler(DurableOccurrenceRecorder recorder)
    : EverTaskHandler<CustomQueueDurableTask>
{
    public const string Queue = "durable-lane";

    public override string? QueueName => Queue;

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(CustomQueueDurableTask backgroundTask, CancellationToken cancellationToken) =>
        recorder.RecordAsync(Context, cancellationToken);
}

/// <summary>
/// The same probe behind a rate limit, for the one invariant that needs a policy: a durable schedule row must
/// not spend the handler's budget, because it never runs the handler.
/// </summary>
public record RateLimitedDurableTask(string Marker) : IEverTask;

public class RateLimitedDurableTaskHandler(DurableOccurrenceRecorder recorder)
    : EverTaskHandler<RateLimitedDurableTask>
{
    public override RateLimitPolicy? RateLimitPolicy { get; } = new(2, TimeSpan.FromHours(1));

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(RateLimitedDurableTask backgroundTask, CancellationToken cancellationToken) =>
        recorder.RecordAsync(Context, cancellationToken);
}

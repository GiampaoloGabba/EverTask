using System.Collections.Concurrent;

namespace EverTask.Tests;

// Tasks and shared state for the runtime schedule-management suite. Registered per test host, like every other
// recorder here, so nothing leaks between tests running in parallel.

/// <summary>
/// Counts the deliveries of a rescheduled series and, when a test asks for it, holds one INSIDE the handler.
/// </summary>
/// <remarks>
/// The hold is what makes the <c>InProgress</c> case reachable: a reschedule is never refused because a run is
/// in flight, and the run that finishes afterwards must lose its compare-and-swap and pick up the new
/// definition instead of writing the next run it had already computed.
/// </remarks>
public sealed class RescheduleRecorder
{
    private readonly TaskCompletionSource _entered  = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _count;

    /// <summary>Completes as soon as a delivery is really inside the handler.</summary>
    public Task Entered => _entered.Task;

    /// <summary>How many deliveries reached the handler.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The run number each delivery reported, in arrival order.</summary>
    public ConcurrentQueue<int> RunNumbers { get; } = new();

    /// <summary>Holds every delivery inside the handler until <see cref="Release"/>.</summary>
    public bool Hold { get; set; }

    public void Release() => _released.TrySetResult();

    public async Task RecordAsync(ITaskExecutionContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        RunNumbers.Enqueue(context.RunNumber);

        if (!Hold)
            return;

        _entered.TrySetResult();
        await _released.Task.WaitAsync(ct);
    }
}

/// <summary>The payload every schedule-management test registers.</summary>
public record RescheduleProbeTask(string Marker) : IEverTask;

public class RescheduleProbeTaskHandler(RescheduleRecorder recorder) : EverTaskHandler<RescheduleProbeTask>
{
    // One quick retry instead of the global three at half a second: a test that makes an occurrence fail wants
    // it Failed, not three seconds of backoff first.
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(RescheduleProbeTask backgroundTask, CancellationToken cancellationToken) =>
        recorder.RecordAsync(Context, cancellationToken);
}

/// <summary>The same probe under a rate limit, for the schedule changes that race the gate.</summary>
public record ThrottledRescheduleProbeTask(string RateLimitKey) : IEverTask, IRateLimitedTask;

/// <summary>
/// One permit an hour with a reservation horizon of a millisecond: the FIRST delivery of a key proceeds and
/// every later one is terminally rejected, which is the only way into
/// <c>QueueNextOccourrence(countsAsRun: false)</c> — the advance path that writes nothing to storage.
/// </summary>
public class ThrottledRescheduleProbeTaskHandler(RescheduleRecorder recorder)
    : EverTaskHandler<ThrottledRescheduleProbeTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromHours(1)) { Burst = 1, MaxReservationHorizon = TimeSpan.FromMilliseconds(1) };

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(ThrottledRescheduleProbeTask backgroundTask, CancellationToken cancellationToken) =>
        recorder.RecordAsync(Context, cancellationToken);
}

/// <summary>The probe whose rate limit DEFERS instead of rejecting.</summary>
public record DeferrableRescheduleProbeTask(string RateLimitKey) : IEverTask, IRateLimitedTask;

/// <summary>
/// One permit a second under the default reservation horizon: the second delivery of a key is re-parked at the
/// slot the limiter reserved for it, which is the path that hands the gate a registration of its own.
/// </summary>
public class DeferrableRescheduleProbeTaskHandler(RescheduleRecorder recorder)
    : EverTaskHandler<DeferrableRescheduleProbeTask>
{
    public override RateLimitPolicy? RateLimitPolicy => new(1, TimeSpan.FromSeconds(1)) { Burst = 1 };

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(DeferrableRescheduleProbeTask backgroundTask, CancellationToken cancellationToken) =>
        recorder.RecordAsync(Context, cancellationToken);
}

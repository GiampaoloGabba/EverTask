using System.Collections.Concurrent;

namespace EverTask.Tests;

// Tasks, providers and shared state for the occurrence-provider suite (phase 6). The provider itself is real
// — resolved from the container, in its own scope, on every question — and everything a test wants to steer
// lives on the singleton probe it depends on, so nothing here is a mock standing in for the seam under test.

/// <summary>
/// The calendar a test lends its provider, plus everything the provider did.
/// </summary>
/// <remarks>
/// Registered as a SINGLETON while the provider itself is scoped, which is the shape a real one has: the
/// implementation is built per call, the thing it reads (a table, a cache) outlives it.
/// </remarks>
public sealed class OccurrenceProviderProbe
{
    private int _calls;

    /// <summary>Every request the provider was handed, in order.</summary>
    public ConcurrentQueue<NextOccurrenceRequest> Requests { get; } = new();

    /// <summary>How many times the provider was asked anything.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>
    /// The grid. Null means "no further occurrence", which ends the series. Defaults to a five-minute grid
    /// anchored on the epoch, which is deterministic and answerable for ANY instant — the two things a
    /// provider has to be.
    /// </summary>
    public Func<NextOccurrenceRequest, DateTimeOffset?> Answer { get; set; } = GridOf(TimeSpan.FromMinutes(5));

    /// <summary>When set, the provider throws it instead of answering. Cleared to heal.</summary>
    public Func<Exception>? Fault { get; set; }

    /// <summary>Fails this many calls and then heals by itself — an outage a test does not have to time.</summary>
    public int FailNextCalls { get; set; }

    /// <summary>
    /// Fails this many CONSTRUCTIONS of the provider and then heals: the shape of a provider that caches its
    /// calendar in its constructor while the database behind it is still coming up.
    /// </summary>
    public int FailNextConstructions;

    /// <summary>
    /// When set, the provider waits on it before answering — a calendar that does not come back. The only way
    /// out is the caller's cancellation token, which is precisely what the test is about.
    /// </summary>
    public SemaphoreSlim? Hang { get; set; }

    private int _cancelledWaits;

    /// <summary>How many times a wait on <see cref="Hang"/> ended because the caller cancelled it.</summary>
    public int CancelledWaits => Volatile.Read(ref _cancelledWaits);

    /// <summary>A grid every <paramref name="step"/> from the epoch: the answer for any instant, in O(1).</summary>
    public static Func<NextOccurrenceRequest, DateTimeOffset?> GridOf(TimeSpan step, DateTimeOffset? anchor = null)
    {
        var origin = anchor ?? DateTimeOffset.UnixEpoch;

        return request =>
        {
            var elapsed = request.AfterUtc - origin;
            var slots   = (long)Math.Floor(elapsed.Ticks / (double)step.Ticks) + 1;

            return origin + TimeSpan.FromTicks(slots * step.Ticks);
        };
    }

    /// <summary>The provider's answer, recording the question and honouring whatever fault is armed.</summary>
    public DateTimeOffset? Ask(NextOccurrenceRequest request)
    {
        Interlocked.Increment(ref _calls);
        Requests.Enqueue(request);

        if (FailNextCalls > 0)
        {
            FailNextCalls--;
            throw Fault?.Invoke() ?? new InvalidOperationException("the calendar database is unavailable");
        }

        if (Fault is { } fault)
            throw fault();

        return Answer(request);
    }

    /// <summary>
    /// <see cref="Ask"/>, with the question recorded BEFORE the provider possibly stops answering: a test that
    /// wants to catch the provider mid-question needs to see the call, not the answer.
    /// </summary>
    public async ValueTask<DateTimeOffset?> AskAsync(NextOccurrenceRequest request, CancellationToken ct)
    {
        if (Hang is not { } gate)
            return Ask(request);

        Interlocked.Increment(ref _calls);
        Requests.Enqueue(request);

        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelledWaits);
            throw;
        }

        return Answer(request);
    }

    /// <summary>Called by a provider's CONSTRUCTOR, so a test can make the provider unbuildable for a while.</summary>
    public void EnterConstructor()
    {
        if (Interlocked.Decrement(ref FailNextConstructions) >= 0)
            throw new InvalidOperationException("the calendar cannot be loaded: its database is unreachable");
    }

    public NextOccurrenceRequest[] Snapshot() => Requests.ToArray();
}

/// <summary>The ordinary case: a provider that does not promise the same answer twice.</summary>
public sealed class ProbeOccurrenceProvider(OccurrenceProviderProbe probe) : INextOccurrenceProvider
{
    public ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                             CancellationToken cancellationToken = default) =>
        probe.AskAsync(request, cancellationToken);
}

/// <summary>
/// The same probe, declared DETERMINISTIC: the only shape <see cref="CatchUpOverflowPolicy.SkipOldest"/>
/// accepts, because keeping the newest slots of a backlog means probing the grid at instants it never
/// returned.
/// </summary>
public sealed class DeterministicProbeProvider(OccurrenceProviderProbe probe) : INextOccurrenceProvider
{
    public bool IsDeterministic => true;

    public ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                             CancellationToken cancellationToken = default) =>
        probe.AskAsync(request, cancellationToken);
}

/// <summary>
/// A deterministic provider that CACHES its calendar in the constructor — what the docs recommend — and can
/// therefore fail to be built at all while the source behind it is down.
/// </summary>
public sealed class FragileProbeProvider : INextOccurrenceProvider
{
    private readonly OccurrenceProviderProbe _probe;

    public FragileProbeProvider(OccurrenceProviderProbe probe)
    {
        probe.EnterConstructor();
        _probe = probe;
    }

    public bool IsDeterministic => true;

    public ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                             CancellationToken cancellationToken = default) =>
        _probe.AskAsync(request, cancellationToken);
}

/// <summary>
/// Which scoped objects a provider call really built and released — the only way "a fresh scope per call" can
/// be observed from outside, since a reused scope answers every question just as correctly.
/// </summary>
/// <remarks>
/// A singleton, like the probe: it has to outlive the scopes it is counting.
/// </remarks>
public sealed class ProviderScopeLedger
{
    private int _disposals;

    /// <summary>One entry per object built, in order. Distinct entries are distinct instances.</summary>
    public ConcurrentQueue<Guid> Built { get; } = new();

    /// <summary>How many of them were released with the scope that built them.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    public void RecordBuilt(Guid instance) => Built.Enqueue(instance);

    public void RecordDisposal() => Interlocked.Increment(ref _disposals);
}

/// <summary>
/// The documented dependency of a provider — "a DbContext or a repository" — in the shape that only implements
/// <see cref="IAsyncDisposable"/>, which is the ordinary one for both.
/// </summary>
public sealed class AsyncOnlyCalendar(ProviderScopeLedger ledger) : IAsyncDisposable
{
    private readonly Guid _instance = Guid.NewGuid();

    /// <summary>What the provider reads off it, so the dependency is really resolved and really used.</summary>
    public bool IsDeterministic => true;

    /// <summary>
    /// Records WHICH calendar answered this call. One entry per call, so a scope that was reused shows up as
    /// two entries carrying the same instance instead of two different ones.
    /// </summary>
    public void NoteUse() => ledger.RecordBuilt(_instance);

    public ValueTask DisposeAsync()
    {
        ledger.RecordDisposal();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A provider whose scoped dependency can only be disposed asynchronously.</summary>
public sealed class AsyncCalendarProvider(OccurrenceProviderProbe probe, AsyncOnlyCalendar calendar)
    : INextOccurrenceProvider
{
    public bool IsDeterministic => calendar.IsDeterministic;

    public ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                             CancellationToken cancellationToken = default)
    {
        calendar.NoteUse();
        return probe.AskAsync(request, cancellationToken);
    }
}

/// <summary>The payload every provider test schedules.</summary>
public record ProviderScheduleTask(string Marker) : IEverTask;

public class ProviderScheduleTaskHandler(DurableOccurrenceRecorder recorder) : EverTaskHandler<ProviderScheduleTask>
{
    // One quick retry instead of the global three at half a second, for the same reason the durable suite
    // overrides it: a test that wants a Failed row does not want three seconds of backoff first.
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(ProviderScheduleTask backgroundTask, CancellationToken cancellationToken) =>
        recorder.RecordAsync(Context, cancellationToken);
}

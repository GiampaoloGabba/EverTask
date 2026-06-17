using System.Diagnostics;
using EverTask.Abstractions;
using EverTask.LoadHarness.Infra;

namespace EverTask.LoadHarness.Tasks;

/// <summary>
/// Carries the dispatch timestamp so the handler can record dispatch→handler latency, plus an OPTIONAL
/// payload string (<c>--payload</c>) so a scenario can size the serialized task body. <c>Payload</c> is
/// null by default (the historical tiny/primitive task), so existing runs are byte-identical; when set it
/// is the same string reference on every dispatch, so the per-task cost is the serializer re-serializing
/// it once at <c>Persist</c> (where Newtonsoft→STJ shows up on the durable path).
/// </summary>
public sealed record CountingTask(long DispatchTicks, string? Payload = null) : IEverTask;

/// <summary>
/// No-op handler that signals completion + records latency through the per-run <see cref="RunContext"/>.
/// EverTask resolves it per task from DI, so the singleton <see cref="RunContext"/> is injected; the
/// scenario swaps the gate/recorder it points at between iterations.
/// </summary>
public sealed class CountingHandler(RunContext ctx) : EverTaskHandler<CountingTask>
{
    public override Task Handle(CountingTask task, CancellationToken cancellationToken)
    {
        // Dispatch → handler-start latency (the channel-reactivity segment; on a real store it includes
        // the SetInProgress write — see BENCHMARK_PLAN §2.3).
        ctx.Current?.Latency.Record(Stopwatch.GetTimestamp() - task.DispatchTicks);
        return Task.CompletedTask;
    }

    // Mark completion only after the FULL lifecycle (handler + SetCompleted write), so L8 throughput
    // counts the durable write tail, not just the handler returning.
    public override ValueTask OnCompleted(Guid persistenceId)
    {
        ctx.Current?.Gate.MarkDoneByThread();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Singleton handed to handlers; holds the gate + latency recorder for the iteration currently running.
/// The handler reads <see cref="Current"/>; the scenario sets it before each dispatch wave.
/// </summary>
public sealed class RunContext
{
    public sealed record Run(CompletionGate Gate, LatencyRecorder Latency);

    private volatile Run? _current;
    public Run? Current { get => _current; set => _current = value; }
}

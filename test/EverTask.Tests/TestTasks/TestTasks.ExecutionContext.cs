using System.Collections.Concurrent;

namespace EverTask.Tests;

// Tasks and shared state for the execution-context suite.
// Modeled on TestTasks.Resilience.cs: register a fresh ExecutionContextRecorder per test host.

/// <summary>
/// A flat copy of what a handler read from its <see cref="ITaskExecutionContext"/> at one point of the
/// lifecycle. Copied rather than kept by reference because <c>Attempt</c> moves while the delivery is alive.
/// </summary>
public sealed record ContextSnapshot(
    string Phase,
    Guid TaskId,
    Guid? ScheduleId,
    string? TaskKey,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? ScheduledAtLocal,
    string? TimeZoneId,
    DateTimeOffset StartedAtUtc,
    int Attempt,
    int RunNumber,
    int ScheduleVersion,
    bool IsRecurring,
    bool IsOccurrence,
    MisfireInfo? Misfire);

/// <summary>Test-local coordination state for the execution-context handlers.</summary>
public sealed class ExecutionContextRecorder
{
    public ConcurrentQueue<ContextSnapshot> Snapshots { get; } = new();

    /// <summary>What the AMBIENT accessor handed to a service that is not the handler, per phase.</summary>
    public ConcurrentQueue<(string Phase, Guid? TaskId)> AmbientReads { get; } = new();

    /// <summary>How many times <see cref="ContextRetryTaskHandler"/> still has to fail before succeeding.</summary>
    public int FailuresToInject;

    public void Record(string phase, ITaskExecutionContext context) =>
        Snapshots.Enqueue(new ContextSnapshot(
            phase,
            context.TaskId,
            context.ScheduleId,
            context.TaskKey,
            context.ScheduledAtUtc,
            context.ScheduledAtLocal,
            context.TimeZoneId,
            context.StartedAtUtc,
            context.Attempt,
            context.RunNumber,
            context.ScheduleVersion,
            context.IsRecurring,
            context.IsOccurrence,
            context.Misfire));

    public ContextSnapshot[] For(string phase) =>
        Snapshots.Where(s => s.Phase == phase).ToArray();

    public ContextSnapshot? Single(string phase) =>
        Snapshots.SingleOrDefault(s => s.Phase == phase);
}

/// <summary>One-shot probe: records the context in every lifecycle callback and in Handle.</summary>
public record ContextProbeTask(string Marker) : IEverTask;

public class ContextProbeTaskHandler(ExecutionContextRecorder recorder) : EverTaskHandler<ContextProbeTask>
{
    public override ValueTask OnStarted(Guid taskId)
    {
        recorder.Record("OnStarted", Context);
        return default;
    }

    public override Task Handle(ContextProbeTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.Record("Handle", Context);
        return Task.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        recorder.Record("OnCompleted", Context);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Handled by a class that implements <see cref="IEverTaskHandler{TTask}"/> without the base class.</summary>
public record RawInterfaceTask : IEverTask;

/// <summary>
/// The handler analogue of <c>ConsumerCompatibilityTests.LegacyMinimalTaskStorage</c>: it implements
/// <see cref="IEverTaskHandler{TTask}"/> DIRECTLY and declares only the members the interface still requires
/// — notably NOT <c>SetExecutionContext</c>.
/// </summary>
/// <remarks>
/// It exists first of all to be COMPILED: the day the context injection stops being a default member, this
/// class fails to build, which is the earliest moment the "keeps compiling unchanged" claim can break. Then
/// it is dispatched for real, so the worker's compiled injector calls that default body on a live delivery
/// instead of it being unreachable code. Everything it records comes from the ambient accessor, the only
/// route to the context a handler without the base class has.
/// </remarks>
public sealed class RawInterfaceTaskHandler(ExecutionContextRecorder recorder, AmbientContextReader reader)
    : IEverTaskHandler<RawInterfaceTask>
{
    public IRetryPolicy? RetryPolicy => null;
    public TimeSpan?     Timeout     => null;
    public string?       QueueName   => null;

    [Obsolete("This property is deprecated and has no effect. EverTask's async/await execution is non-blocking and suitable for all workloads. For CPU-intensive synchronous operations, use Task.Run within your handler instead.")]
    public bool CpuBoundOperation { get; set; }

    public Task Handle(RawInterfaceTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.AmbientReads.Enqueue(("raw-Handle", reader.Read()?.TaskId));
        return Task.CompletedTask;
    }

    public ValueTask OnStarted(Guid persistenceId)
    {
        recorder.AmbientReads.Enqueue(("raw-OnStarted", reader.Read()?.TaskId));
        return default;
    }

    public ValueTask OnCompleted(Guid persistenceId)
    {
        recorder.AmbientReads.Enqueue(("raw-OnCompleted", reader.Read()?.TaskId));
        return default;
    }

    public ValueTask OnError(Guid persistenceId, Exception? exception, string? message) => default;

    public ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay) => default;

    public void SetLogCapture(ITaskLogCapture logCapture) { }

    public ValueTask DisposeAsync() => default;
}

/// <summary>Handled by a direct implementor that takes the context by implementing the member itself.</summary>
public record RawInterfaceContextTask : IEverTask;

/// <summary>
/// The other half of the contract: a direct implementor may replace the empty default body with its own.
/// Nothing is recorded unless the worker's injector really lands on this class's member, which is what makes
/// the sibling handler's silence a proof that the SAME call reached the interface's default body.
/// </summary>
public sealed class RawInterfaceContextTaskHandler(ExecutionContextRecorder recorder)
    : IEverTaskHandler<RawInterfaceContextTask>
{
    private ITaskExecutionContext? _context;

    public IRetryPolicy? RetryPolicy => null;
    public TimeSpan?     Timeout     => null;
    public string?       QueueName   => null;

    [Obsolete("This property is deprecated and has no effect. EverTask's async/await execution is non-blocking and suitable for all workloads. For CPU-intensive synchronous operations, use Task.Run within your handler instead.")]
    public bool CpuBoundOperation { get; set; }

    public void SetExecutionContext(ITaskExecutionContext context) => _context = context;

    public Task Handle(RawInterfaceContextTask backgroundTask, CancellationToken cancellationToken)
    {
        Record("raw-context-Handle");
        return Task.CompletedTask;
    }

    public ValueTask OnStarted(Guid persistenceId)
    {
        Record("raw-context-OnStarted");
        return default;
    }

    public ValueTask OnCompleted(Guid persistenceId) => default;

    public ValueTask OnError(Guid persistenceId, Exception? exception, string? message) => default;

    public ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay) => default;

    public void SetLogCapture(ITaskLogCapture logCapture) { }

    public ValueTask DisposeAsync() => default;

    private void Record(string phase) =>
        recorder.Record(phase, _context ?? throw new InvalidOperationException(
            "the worker never injected the execution context into a direct implementor of IEverTaskHandler<T>"));
}

/// <summary>Fails a configurable number of times so the attempt number can be observed across retries.</summary>
public record ContextRetryTask : IEverTask;

public class ContextRetryTaskHandler(ExecutionContextRecorder recorder) : EverTaskHandler<ContextRetryTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(20));

    public override Task Handle(ContextRetryTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.Record("Handle", Context);

        if (Interlocked.Decrement(ref recorder.FailuresToInject) >= 0)
            throw new InvalidOperationException("injected failure");

        return Task.CompletedTask;
    }

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        recorder.Record("OnRetry", Context);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        recorder.Record("OnError", Context);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Always fails, and cancels itself from <c>OnRetry</c>: the retry the callback announces never starts.
/// </summary>
public record ContextCancelledRetryTask : IEverTask;

public class ContextCancelledRetryTaskHandler(ExecutionContextRecorder recorder, ITaskDispatcher dispatcher)
    : EverTaskHandler<ContextCancelledRetryTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(20));

    public override Task Handle(ContextCancelledRetryTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.Record("Handle", Context);
        throw new InvalidOperationException("injected failure");
    }

    public override async ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        recorder.Record("OnRetry", Context);

        // The retry policy re-checks the token at the top of its loop, so the attempt just announced is
        // abandoned before the handler is ever entered again.
        await dispatcher.Cancel(taskId);
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        recorder.Record("OnError", Context);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One permit per 5 s with a 1 s reservation horizon: a second dispatch is TERMINALLY rejected by the gate,
/// the one lifecycle path that reaches <c>OnError</c> without ever entering the execution core.
/// </summary>
public record RateLimitRejectedContextTask(int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => "execution-context-rejection";
}

public class RateLimitRejectedContextTaskHandler(ExecutionContextRecorder recorder, AmbientContextReader reader)
    : EverTaskHandler<RateLimitRejectedContextTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(5)) { Burst = 1, MaxReservationHorizon = TimeSpan.FromSeconds(1) };

    public override Task Handle(RateLimitRejectedContextTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.Record($"Handle-{backgroundTask.Index}", Context);
        return Task.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        // Both per-delivery injections, read the way the docs invite: an uninjected Context throws and an
        // uninjected Logger is null, and either one would be swallowed by the worker's callback guard —
        // leaving nothing recorded below.
        Logger.LogError(exception, "rejected task {TaskId} stood for slot {Slot}", taskId, Context.ScheduledAtUtc);

        recorder.AmbientReads.Enqueue(("OnError", reader.Read()?.TaskId));
        recorder.Record("OnError", Context);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Recurring probe: one snapshot per occurrence, in Handle and in every callback.</summary>
public record ContextRecurringTask : IEverTask;

public class ContextRecurringTaskHandler(ExecutionContextRecorder recorder) : EverTaskHandler<ContextRecurringTask>
{
    public override ValueTask OnStarted(Guid taskId)
    {
        recorder.Record("OnStarted", Context);
        return ValueTask.CompletedTask;
    }

    public override Task Handle(ContextRecurringTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.Record("Handle", Context);
        return Task.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        recorder.Record("OnCompleted", Context);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A SCOPED service that reaches the context the only way a service can: through the ambient accessor.
/// </summary>
/// <remarks>
/// Its lifetime is the point of the test. Injected into an eager handler it is built in the DISPATCHER's
/// scope, before the delivery exists; injected into a lazy one, in the worker's per-task scope. The ambient
/// accessor has to answer correctly in both.
/// </remarks>
public sealed class AmbientContextReader(ITaskExecutionContextAccessor accessor)
{
    public ITaskExecutionContext? Read() => accessor.Current;
}

/// <summary>Records what a scoped dependency — not the handler — sees through the ambient accessor.</summary>
public record AmbientContextTask(string Phase) : IEverTask;

public class AmbientContextTaskHandler(ExecutionContextRecorder recorder, AmbientContextReader reader)
    : EverTaskHandler<AmbientContextTask>
{
    public override Task Handle(AmbientContextTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.AmbientReads.Enqueue((backgroundTask.Phase, reader.Read()?.TaskId));
        recorder.Record(backgroundTask.Phase, Context);
        return Task.CompletedTask;
    }
}

/// <summary>One permit per 900 ms on a single key: the second delivery of the pair is always deferred.</summary>
public record RateLimitedContextTask(int Index) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => "execution-context";
}

public class RateLimitedContextTaskHandler(ExecutionContextRecorder recorder)
    : EverTaskHandler<RateLimitedContextTask>
{
    public override RateLimitPolicy? RateLimitPolicy => new(1, TimeSpan.FromMilliseconds(900)) { Burst = 1 };

    public override Task Handle(RateLimitedContextTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.Record($"Handle-{backgroundTask.Index}", Context);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Always fails, and its retry is turned back by the rate-limit gate before it can enter the handler again:
/// the first attempt takes the single permit of a 30 s window and <c>Discard</c> refuses the retry outright
/// (<c>ThrottleRetries</c> defaults to true). The rejection is terminal, so the delivery ends in
/// <c>OnError</c> — with an attempt number that must not count the retry the gate never admitted.
/// </summary>
public record ContextThrottledRetryTask : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => "execution-context-throttled-retry";
}

public class ContextThrottledRetryTaskHandler(ExecutionContextRecorder recorder)
    : EverTaskHandler<ContextThrottledRetryTask>
{
    public override RateLimitPolicy? RateLimitPolicy =>
        new(1, TimeSpan.FromSeconds(30))
        {
            Burst            = 1,
            OverflowBehavior = RateLimitOverflowBehavior.Discard
        };

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(20));

    public override Task Handle(ContextThrottledRetryTask backgroundTask, CancellationToken cancellationToken)
    {
        recorder.Record("Handle", Context);
        throw new InvalidOperationException("injected failure");
    }

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        recorder.Record("OnRetry", Context);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        recorder.Record("OnError", Context);
        return ValueTask.CompletedTask;
    }
}

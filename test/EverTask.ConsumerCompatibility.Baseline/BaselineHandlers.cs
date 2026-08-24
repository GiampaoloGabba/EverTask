using System.Collections.Concurrent;
using EverTask.Abstractions;

namespace EverTask.ConsumerCompatibility.Baseline;

/// <summary>
/// Where the handlers below write what they did, so the test can read it back.
/// </summary>
/// <remarks>
/// A singleton the test registers, rather than static state: the handlers are built and disposed by the
/// container like any other, and two hosts in the same process never read each other's phases.
/// </remarks>
public sealed class BaselineHandlerProbe
{
    public ConcurrentQueue<string> Phases { get; } = new();

    public void Record(string phase) => Phases.Enqueue(phase);
}

/// <summary>The task of the handler that implements the interface directly.</summary>
public sealed record BaselineRawInterfaceTask : IEverTask;

/// <summary>
/// A handler that implements <see cref="IEverTaskHandler{TTask}"/> DIRECTLY, declaring exactly the members
/// the interface required before the execution context existed — no <c>SetExecutionContext</c> anywhere, and
/// nothing that could know the context exists.
/// </summary>
/// <remarks>
/// The in-tree twin (<c>RawInterfaceTaskHandler</c>) is recompiled against the new sources, so it shows that
/// such a handler still COMPILES. This one is compiled here, against the baseline metadata, and executed by
/// the test against the current assemblies, which is the other half: the CLR builds this type's interface map
/// against today's interface, so had the new member arrived abstract instead of as a default one, merely
/// resolving the handler would throw a <see cref="TypeLoadException"/> — and the injector's call reaches the
/// default body of an assembly that was never rebuilt.
/// </remarks>
public sealed class BaselineRawInterfaceTaskHandler(BaselineHandlerProbe probe)
    : IEverTaskHandler<BaselineRawInterfaceTask>
{
    public IRetryPolicy? RetryPolicy => null;
    public TimeSpan?     Timeout     => null;
    public string?       QueueName   => null;

    [Obsolete("This property is deprecated and has no effect. EverTask's async/await execution is non-blocking and suitable for all workloads. For CPU-intensive synchronous operations, use Task.Run within your handler instead.")]
    public bool CpuBoundOperation { get; set; }

    public Task Handle(BaselineRawInterfaceTask backgroundTask, CancellationToken cancellationToken)
    {
        probe.Record("raw-Handle");
        return Task.CompletedTask;
    }

    public ValueTask OnStarted(Guid persistenceId)
    {
        probe.Record("raw-OnStarted");
        return default;
    }

    public ValueTask OnCompleted(Guid persistenceId)
    {
        probe.Record("raw-OnCompleted");
        return default;
    }

    public ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        // Never expected. Recorded rather than swallowed so a failing delivery names its own cause instead
        // of leaving the test to report a missing phase.
        probe.Record($"raw-OnError: {message} {exception}");
        return default;
    }

    public ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay) => default;

    public void SetLogCapture(ITaskLogCapture logCapture) { }

    public ValueTask DisposeAsync() => default;
}

/// <summary>The task of the handler written the way the documentation recommends.</summary>
public sealed record BaselineBaseClassTask : IEverTask;

/// <summary>
/// The ordinary case: a handler deriving from <see cref="EverTaskHandler{TTask}"/>, compiled when that base
/// class had neither the <c>Context</c> property nor the explicit member that fills it.
/// </summary>
/// <remarks>
/// Both additions are inherited rather than declared, so this type never mentions them — which is precisely
/// what has to keep working. It overrides the three lifecycle members an application usually does, so the
/// virtual slots they bind to are exercised too.
/// </remarks>
public sealed class BaselineBaseClassTaskHandler(BaselineHandlerProbe probe) : EverTaskHandler<BaselineBaseClassTask>
{
    public override ValueTask OnStarted(Guid taskId)
    {
        probe.Record("base-OnStarted");
        return default;
    }

    public override Task Handle(BaselineBaseClassTask backgroundTask, CancellationToken cancellationToken)
    {
        probe.Record("base-Handle");
        return Task.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        probe.Record("base-OnCompleted");
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        probe.Record($"base-OnError: {message} {exception}");
        return ValueTask.CompletedTask;
    }
}

using System.Collections.Concurrent;

namespace EverTask.Tests;

// Test tasks/handlers for DI registration robustness (M7 G1/G2/G3).

// ── G3: eager handler must not be shared across concurrent dispatches ──────────────────────────

public record EagerSharedStateTask(int Index) : IEverTask;

/// <summary>
/// Records the identity of the handler instance that runs each execution. If a manual singleton
/// registration of the interface is shared across concurrent eager dispatches, every execution sees
/// the SAME instance — which is corruption, because the worker sets per-execution state
/// (<c>SetLogCapture</c>) on the handler.
/// </summary>
public class EagerSharedStateHandler : EverTaskHandler<EagerSharedStateTask>
{
    private readonly Guid _instanceId = Guid.NewGuid();

    public static ConcurrentBag<Guid> SeenInstanceIds = new();
    public static CountdownEvent?      Countdown;

    public static void Reset(int expectedExecutions)
    {
        SeenInstanceIds = new ConcurrentBag<Guid>();
        Countdown       = new CountdownEvent(expectedExecutions);
    }

    public override Task Handle(EagerSharedStateTask backgroundTask, CancellationToken cancellationToken)
    {
        SeenInstanceIds.Add(_instanceId);
        Countdown?.Signal();
        return Task.CompletedTask;
    }
}

// ── G1: open-generic handlers are not supported (must be detected, not silently dropped) ────────

public record OpenGenericRegistrationTask<T>(T Payload) : IEverTask;

public class OpenGenericRegistrationHandler<T> : EverTaskHandler<OpenGenericRegistrationTask<T>>
{
    public override Task Handle(OpenGenericRegistrationTask<T> backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

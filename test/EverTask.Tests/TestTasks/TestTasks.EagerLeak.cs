using System.Collections.Concurrent;

namespace EverTask.Tests;

// Test task for the eager root-pinning regression (L27): a short-delayed task takes the EAGER path,
// so the handler instance is resolved at dispatch and carried to execution. With the fix it is
// resolved into an EverTask-owned scope disposed right after execution, so nothing stays pinned in
// the root container. Do NOT reuse elsewhere: the static tracker must stay isolated.

public record TestTaskEagerTracked() : IEverTask;

public class TestTaskEagerTrackedHandler : EverTaskHandler<TestTaskEagerTracked>
{
    // Weak references: after dispatch + execution + GC nothing must keep instances alive.
    public static readonly ConcurrentBag<WeakReference> Instances = new();

    public TestTaskEagerTrackedHandler()
    {
        Instances.Add(new WeakReference(this));
    }

    public override Task Handle(TestTaskEagerTracked backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

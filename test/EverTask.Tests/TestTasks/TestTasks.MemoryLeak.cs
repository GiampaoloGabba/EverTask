using System.Collections.Concurrent;

namespace EverTask.Tests;

// Test tasks for the MEM-2 memory regression suite (MemoryLeakRegressionTests).
// Do NOT reuse these in other tests: the static trackers must stay isolated.

public record TestTaskMem2Tracked() : IEverTask;

public class TestTaskMem2TrackedHandler : EverTaskHandler<TestTaskMem2Tracked>
{
    // Weak references: after dispatch + execution + GC nothing must keep instances alive
    public static readonly ConcurrentBag<WeakReference> Instances = new();

    public TestTaskMem2TrackedHandler()
    {
        Instances.Add(new WeakReference(this));
    }

    public override Task Handle(TestTaskMem2Tracked backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public record TestTaskMem2DisposeProbe() : IEverTask;

public class TestTaskMem2DisposeProbeHandler : EverTaskHandler<TestTaskMem2DisposeProbe>
{
    private static int _created;
    private static int _disposed;
    private static int _executed;

    public static int Created  => Volatile.Read(ref _created);
    public static int Disposed => Volatile.Read(ref _disposed);
    public static int Executed => Volatile.Read(ref _executed);

    public static void Reset()
    {
        Volatile.Write(ref _created, 0);
        Volatile.Write(ref _disposed, 0);
        Volatile.Write(ref _executed, 0);
    }

    public TestTaskMem2DisposeProbeHandler()
    {
        Interlocked.Increment(ref _created);
    }

    public override Task Handle(TestTaskMem2DisposeProbe backgroundTask, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executed);
        return Task.CompletedTask;
    }

    protected override ValueTask DisposeAsyncCore()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }
}

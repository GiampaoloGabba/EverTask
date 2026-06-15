using EverTask.Abstractions;

namespace EverTask.Tests;

// Dedicated, unique handler type for the F23 lifecycle-reflection gate: only the P-B hot-path tests
// dispatch it, so WorkerExecutor.LifecycleReflectionResolutions advances exactly once for this type
// regardless of what other tests run concurrently in the process.
public record PerfLifecycleProbeTask : IEverTask;

public class PerfLifecycleProbeHandler : EverTaskHandler<PerfLifecycleProbeTask>
{
    public override Task Handle(PerfLifecycleProbeTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

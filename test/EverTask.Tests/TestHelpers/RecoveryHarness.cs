using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Storage;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Builds the REAL <see cref="WorkerService"/> around a given storage, so a test can run startup recovery
/// exactly as a host would — pagination, the two waves, the L18 accounting and the poison guards all execute.
/// </summary>
/// <remarks>
/// Only the collaborators recovery does not decide anything with are stand-ins: the queue manager and the
/// worker executor are never reached on these paths, and the dispatcher is the observation point (its default
/// mock simply succeeds). The storage is whatever the caller passes, and that is the part under test.
/// <para>
/// Internal, and shared with <c>EverTask.Tests.Storage</c> through this assembly's InternalsVisibleTo:
/// <see cref="WorkerService"/> is internal to EverTask, so no public signature can hand it back.
/// </para>
/// </remarks>
internal static class RecoveryHarness
{
    internal static WorkerService CreateRecoveryService(ITaskStorage storage, int maxAttempts = 5,
                                                        ITaskDispatcherInternal? dispatcher = null,
                                                        IEverTaskLogger<WorkerService>? logger = null)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(ITaskStorage))).Returns(storage);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new WorkerService(
            new Mock<IWorkerQueueManager>().Object,
            scopeFactory.Object,
            dispatcher ?? new Mock<ITaskDispatcherInternal>().Object,
            new EverTaskServiceConfiguration(),
            new Mock<IEverTaskWorkerExecutor>().Object,
            logger ?? new Mock<IEverTaskLogger<WorkerService>>().Object)
        {
            MaxRecoveryDispatchAttempts = maxAttempts
        };
    }
}

using EverTask.Storage;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Base class for isolated integration tests. Each test gets its own IHost, storage, and state.
/// Ensures zero state sharing between tests for parallel execution safety.
/// </summary>
public abstract class IsolatedIntegrationTestBase : IAsyncDisposable
{
    protected IHost? Host { get; private set; }
    protected ITaskDispatcher Dispatcher { get; private set; } = null!;
    protected ITaskStorage Storage { get; private set; } = null!;
    protected IWorkerQueue WorkerQueue { get; private set; } = null!;
    protected IWorkerBlacklist WorkerBlacklist { get; private set; } = null!;
    protected IEverTaskWorkerExecutor WorkerExecutor { get; private set; } = null!;
    protected ICancellationSourceProvider CancellationSourceProvider { get; private set; } = null!;
    protected TestTaskStateManager StateManager { get; private set; } = null!;
    protected IGuidGenerator GuidGenerator { get; private set; } = null!;
    protected TaskDeliveryRegistry DeliveryRegistry { get; private set; } = null!;

    private const int DefaultStopTimeoutMs = 2000;

    /// <summary>
    /// Creates an ISOLATED host for this test. MUST be called at the start of each test method.
    /// Each test gets its own IHost instance with scoped services for complete isolation.
    /// </summary>
    /// <param name="channelCapacity">Channel capacity (default: 3)</param>
    /// <param name="maxDegreeOfParallelism">Max parallelism (default: 3)</param>
    /// <param name="configureEverTask">Optional EverTask configuration</param>
    /// <param name="configureServices">Optional additional service configuration</param>
    /// <returns>Started IHost instance</returns>
    protected async Task<IHost> CreateIsolatedHostAsync(
        int channelCapacity = 3,
        int maxDegreeOfParallelism = 3,
        Action<EverTaskServiceConfiguration>? configureEverTask = null,
        Action<IServiceCollection>? configureServices = null)
    {
        // Ensure any previous host is properly disposed before creating new one
        if (Host != null)
        {
            await StopHostAsync();
            Host.Dispose();
            Host = null;
        }

        Host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();

                // Configure EverTask with memory storage
                services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                       .SetChannelOptions(channelCapacity)
                       .SetMaxDegreeOfParallelism(maxDegreeOfParallelism);

                    configureEverTask?.Invoke(cfg);
                })
                .AddMemoryStorage();  // Singleton storage (shared within this test's IHost only)

                // TestTaskStateManager as Singleton (shared within this test's IHost only)
                services.AddSingleton<TestTaskStateManager>();

                // Additional custom configuration
                configureServices?.Invoke(services);
            })
            .Build();

        // Resolve services from the newly created host
        Dispatcher = Host.Services.GetRequiredService<ITaskDispatcher>();
        Storage = Host.Services.GetRequiredService<ITaskStorage>();
        WorkerQueue = Host.Services.GetRequiredService<IWorkerQueue>();
        WorkerBlacklist = Host.Services.GetRequiredService<IWorkerBlacklist>();
        WorkerExecutor = Host.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        CancellationSourceProvider = Host.Services.GetRequiredService<ICancellationSourceProvider>();
        StateManager = Host.Services.GetRequiredService<TestTaskStateManager>();
        GuidGenerator = Host.Services.GetRequiredService<IGuidGenerator>();
        DeliveryRegistry = Host.Services.GetRequiredService<TaskDeliveryRegistry>();

        // Start the host
        await Host.StartAsync();

        return Host;
    }

    /// <summary>
    /// Creates an isolated host with custom builder configuration
    /// </summary>
    protected async Task<IHost> CreateIsolatedHostWithBuilderAsync(
        Action<EverTaskServiceBuilder> configureBuilder,
        bool startHost = true,
        Action<EverTaskServiceConfiguration>? configureEverTask = null)
    {
        // Ensure any previous host is properly disposed before creating new one
        // (a restart simulation must not leave two live hosts on the same storage)
        if (Host != null)
        {
            await StopHostAsync();
            Host.Dispose();
            Host = null;
        }

        Host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();

                var builder = services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly);
                    configureEverTask?.Invoke(cfg);
                });

                configureBuilder(builder);

                // TestTaskStateManager as Singleton (shared within this test's IHost only)
                services.AddSingleton<TestTaskStateManager>();
            })
            .Build();

        Dispatcher = Host.Services.GetRequiredService<ITaskDispatcher>();
        Storage = Host.Services.GetRequiredService<ITaskStorage>();
        WorkerQueue = Host.Services.GetRequiredService<IWorkerQueue>();
        WorkerBlacklist = Host.Services.GetRequiredService<IWorkerBlacklist>();
        WorkerExecutor = Host.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        CancellationSourceProvider = Host.Services.GetRequiredService<ICancellationSourceProvider>();
        StateManager = Host.Services.GetRequiredService<TestTaskStateManager>();
        GuidGenerator = Host.Services.GetRequiredService<IGuidGenerator>();
        DeliveryRegistry = Host.Services.GetRequiredService<TaskDeliveryRegistry>();

        if (startHost)
        {
            await Host.StartAsync();
        }

        return Host;
    }

    /// <summary>
    /// Stops the host with timeout for graceful shutdown
    /// </summary>
    protected async Task StopHostAsync(int timeoutMs = DefaultStopTimeoutMs)
    {
        if (Host == null)
            return;

        var cts = new CancellationTokenSource();
        cts.CancelAfter(timeoutMs);

        try
        {
            await Host.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred, host may not have stopped gracefully
        }
    }

    /// <summary>
    /// Helper: Waits for task to reach expected status
    /// </summary>
    protected async Task<QueuedTask> WaitForTaskStatusAsync(
        Guid taskId,
        QueuedTaskStatus expectedStatus,
        int timeoutMs = 12000)
    {
        return await TaskWaitHelper.WaitForTaskStatusAsync(Storage, taskId, expectedStatus, timeoutMs);
    }

    /// <summary>
    /// Helper: Waits for a task to be accepted by the dispatch pipeline. Use this for tasks whose
    /// first occurrence is due immediately, where waiting for the transient WaitingQueue status is a
    /// race against the scheduler (see <see cref="TaskWaitHelper.WaitForTaskAcceptedAsync"/>).
    /// </summary>
    protected async Task<QueuedTask> WaitForTaskAcceptedAsync(Guid taskId, int timeoutMs = 5000)
    {
        return await TaskWaitHelper.WaitForTaskAcceptedAsync(Storage, taskId, timeoutMs);
    }

    /// <summary>
    /// Helper: Waits until the delivery of <paramref name="taskId"/> is no longer in flight.
    /// <para>
    /// <c>TaskDeliveryRegistry.End</c> is the LAST act of <c>WorkerExecutor.DoWork</c>, after the
    /// outcome is persisted and after the finally's QueueNextOccourrence. That makes it the boundary
    /// two kinds of test need and cannot get from a status. A negative assertion (nothing overwrites
    /// the status, no next occurrence is scheduled) has no event of its own to poll: what CAN be
    /// waited for is the end of the window in which the forbidden write would happen. And an
    /// immediate one-shot re-dispatch of the same taskKey is DISCARDED by the dispatcher while the
    /// previous delivery is registered (it returns the existing id instead of replacing the row), so
    /// a test that re-dispatches a key after a terminal status must wait this window out first.
    /// </para>
    /// </summary>
    protected Task WaitForDeliveryToEndAsync(Guid taskId, int timeoutMs = 10000) =>
        TaskWaitHelper.WaitForConditionAsync(() => !DeliveryRegistry.IsDelivering(taskId), timeoutMs);

    /// <summary>
    /// Helper: Waits for a specific number of tasks in storage
    /// </summary>
    protected async Task<QueuedTask[]> WaitForTaskCountAsync(int expectedCount, int timeoutMs = 5000)
    {
        return await TaskWaitHelper.WaitForTaskCountAsync(Storage, expectedCount, timeoutMs);
    }

    /// <summary>
    /// Helper: Waits for a specific number of pending tasks
    /// </summary>
    protected async Task<QueuedTask[]> WaitForPendingCountAsync(int expectedCount, int timeoutMs = 5000)
    {
        return await TaskWaitHelper.WaitForPendingCountAsync(Storage, expectedCount, timeoutMs);
    }

    /// <summary>
    /// Helper: Waits for recurring task to complete expected runs
    /// </summary>
    protected async Task<QueuedTask> WaitForRecurringRunsAsync(
        Guid taskId,
        int expectedRuns,
        int timeoutMs = 10000)
    {
        return await TaskWaitHelper.WaitForRecurringRunsAsync(Storage, taskId, expectedRuns, timeoutMs);
    }

    /// <summary>
    /// Disposes the host and cleans up resources
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (Host != null)
        {
            await StopHostAsync();
            Host.Dispose();
            Host = null;
        }

        GC.SuppressFinalize(this);
    }
}

using EverTask.Storage;
using EverTask.Worker;

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

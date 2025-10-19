using EverTask.Storage;
using EverTask.Worker;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Base class for integration tests providing common setup and utilities
/// </summary>
public abstract class IntegrationTestBase : IAsyncDisposable
{
    protected IHost? Host { get; private set; }
    protected ITaskDispatcher? Dispatcher { get; private set; }
    protected ITaskStorage? Storage { get; private set; }
    protected IWorkerQueue? WorkerQueue { get; private set; }
    protected IWorkerBlacklist? WorkerBlacklist { get; private set; }
    protected IEverTaskWorkerExecutor? WorkerExecutor { get; private set; }
    protected ICancellationSourceProvider? CancellationSourceProvider { get; private set; }
    protected TestTaskStateManager? StateManager { get; private set; }

    private const int DefaultStopTimeoutMs = 2000;

    /// <summary>
    /// Creates a standard integration test host with default configuration
    /// </summary>
    protected IHost CreateHost(
        int channelCapacity = 3,
        int maxDegreeOfParallelism = 3,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(channelCapacity)
                    .SetMaxDegreeOfParallelism(maxDegreeOfParallelism))
                    .AddMemoryStorage();

                services.AddSingleton<ITaskStorage, MemoryTaskStorage>();
                services.AddSingleton<TestTaskStateManager>();

                configureServices?.Invoke(services);
            })
            .Build();

        return host;
    }

    /// <summary>
    /// Creates an integration test host with custom EverTaskServiceBuilder configuration
    /// </summary>
    protected IHost CreateHostWithBuilder(Action<EverTaskServiceBuilder> configureBuilder)
    {
        var host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging();
                var builder = services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly));

                configureBuilder(builder);

                services.AddSingleton<TestTaskStateManager>();
            })
            .Build();

        return host;
    }

    /// <summary>
    /// Initializes the host and retrieves common services
    /// </summary>
    protected void InitializeHost(
        int channelCapacity = 3,
        int maxDegreeOfParallelism = 3,
        Action<IServiceCollection>? configureServices = null)
    {
        Host = CreateHost(channelCapacity, maxDegreeOfParallelism, configureServices);

        Dispatcher = Host.Services.GetRequiredService<ITaskDispatcher>();
        Storage = Host.Services.GetRequiredService<ITaskStorage>();
        WorkerQueue = Host.Services.GetRequiredService<IWorkerQueue>();
        WorkerBlacklist = Host.Services.GetRequiredService<IWorkerBlacklist>();
        WorkerExecutor = Host.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        CancellationSourceProvider = Host.Services.GetRequiredService<ICancellationSourceProvider>();
        StateManager = Host.Services.GetRequiredService<TestTaskStateManager>();
    }

    /// <summary>
    /// Initializes the host with custom builder configuration and retrieves common services
    /// </summary>
    protected void InitializeHostWithBuilder(Action<EverTaskServiceBuilder> configureBuilder)
    {
        Host = CreateHostWithBuilder(configureBuilder);

        Dispatcher = Host.Services.GetRequiredService<ITaskDispatcher>();
        Storage = Host.Services.GetRequiredService<ITaskStorage>();
        WorkerQueue = Host.Services.GetRequiredService<IWorkerQueue>();
        WorkerBlacklist = Host.Services.GetRequiredService<IWorkerBlacklist>();
        WorkerExecutor = Host.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        CancellationSourceProvider = Host.Services.GetRequiredService<ICancellationSourceProvider>();
        StateManager = Host.Services.GetRequiredService<TestTaskStateManager>();
    }

    /// <summary>
    /// Starts the host
    /// </summary>
    protected async Task StartHostAsync()
    {
        if (Host == null)
            throw new InvalidOperationException("Host not initialized. Call InitializeHost first.");

        await Host.StartAsync();
    }

    /// <summary>
    /// Stops the host with a timeout for graceful shutdown
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
    /// Waits for a task to reach expected status
    /// </summary>
    protected async Task<QueuedTask> WaitForTaskStatusAsync(
        Guid taskId,
        QueuedTaskStatus expectedStatus,
        int timeoutMs = 5000)
    {
        if (Storage == null)
            throw new InvalidOperationException("Storage not initialized.");

        return await TaskWaitHelper.WaitForTaskStatusAsync(Storage, taskId, expectedStatus, timeoutMs);
    }

    /// <summary>
    /// Waits for a specific number of tasks in storage
    /// </summary>
    protected async Task<QueuedTask[]> WaitForTaskCountAsync(int expectedCount, int timeoutMs = 5000)
    {
        if (Storage == null)
            throw new InvalidOperationException("Storage not initialized.");

        return await TaskWaitHelper.WaitForTaskCountAsync(Storage, expectedCount, timeoutMs);
    }

    /// <summary>
    /// Waits for a specific number of pending tasks
    /// </summary>
    protected async Task<QueuedTask[]> WaitForPendingCountAsync(int expectedCount, int timeoutMs = 5000)
    {
        if (Storage == null)
            throw new InvalidOperationException("Storage not initialized.");

        return await TaskWaitHelper.WaitForPendingCountAsync(Storage, expectedCount, timeoutMs);
    }

    /// <summary>
    /// Waits for a recurring task to complete expected number of runs
    /// </summary>
    protected async Task<QueuedTask> WaitForRecurringRunsAsync(
        Guid taskId,
        int expectedRuns,
        int timeoutMs = 10000)
    {
        if (Storage == null)
            throw new InvalidOperationException("Storage not initialized.");

        return await TaskWaitHelper.WaitForRecurringRunsAsync(Storage, taskId, expectedRuns, timeoutMs);
    }

    /// <summary>
    /// Resets the state manager (useful between tests)
    /// </summary>
    protected void ResetState()
    {
        StateManager?.ResetAll();
    }

    /// <summary>
    /// Disposes the host
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        await StopHostAsync();
        Host?.Dispose();
        GC.SuppressFinalize(this);
    }
}

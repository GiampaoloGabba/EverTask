using EverTask.Scheduler;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

// Disable parallel execution for this test class because tests share TestTaskStateManager singleton
[Collection("ShardedSchedulerTests")]
public class ShardedSchedulerIntegrationTests
{
    private IHost _host = null!;
    private ITaskDispatcher _dispatcher = null!;
    private ITaskStorage _storage = null!;
    private TestTaskStateManager _stateManager = null!;

    [Fact]
    public void Should_Configure_Via_Fluent_API()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(cfg => cfg
            .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
            .UseShardedScheduler(shardCount: 8))
            .AddMemoryStorage();

        var provider = services.BuildServiceProvider();

        // Act
        var scheduler = provider.GetRequiredService<IScheduler>();

        // Assert
        scheduler.ShouldBeOfType<ShardedScheduler>();
    }

    [Fact]
    public async Task Should_Handle_High_Load_Scheduling()
    {
        // Arrange
        _host = CreateHost(useShardedScheduler: true, shardCount: 8);
        await _host.StartAsync();

        // Act - Schedule 1000 tasks rapidly (simula spike)
        var scheduleTasks = new List<Task<Guid>>();
        for (int i = 0; i < 1000; i++)
        {
            scheduleTasks.Add(
                _dispatcher.Dispatch(
                    new TestTaskConcurrent1(),
                    DateTimeOffset.UtcNow.AddSeconds(1 + (i % 60))
                )
            );
        }

        // Assert - All Schedule() calls must complete without exceptions
        await Task.WhenAll(scheduleTasks);

        // Verify persistence
        var allTasks = await _storage.GetAll();
        allTasks.Length.ShouldBe(1000);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_Process_Scheduled_Tasks_From_All_Shards()
    {
        // Arrange
        _host = CreateHost(useShardedScheduler: true, shardCount: 4);
        await _host.StartAsync();

        // Act - Schedule 20 tasks with execution time spread across 1-2 seconds
        var taskIds = new List<Guid>();
        for (int i = 0; i < 20; i++)
        {
            var taskId = await _dispatcher.Dispatch(
                new TestTaskConcurrent1(),
                DateTimeOffset.UtcNow.AddSeconds(1 + (i % 2))
            );
            taskIds.Add(taskId);
        }

        // Wait for all tasks to be executed (verify via isolated storage, not shared state manager)
        await TaskWaitHelper.WaitUntilAsync(
            async () => await _storage.GetAll(),
            tasks => tasks.Count(t => taskIds.Contains(t.Id) && t.Status == QueuedTaskStatus.Completed) >= 20,
            timeoutMs: 10000
        );

        // Assert - Verify exactly 20 tasks in THIS test's storage
        var completedTasks = await _storage.GetAll();
        completedTasks.Length.ShouldBe(20);
        completedTasks.ShouldAllBe(t => t.Status == QueuedTaskStatus.Completed);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_Handle_Recurring_Tasks_With_Shards()
    {
        // Arrange
        _host = CreateHost(useShardedScheduler: true, shardCount: 8);
        await _host.StartAsync();

        // Act - Schedule recurring task
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(5)
        );

        // Assert - Wait for 5 executions
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, expectedRuns: 5, timeoutMs: 15000);

        var tasks = await _storage.GetAll();
        tasks[0].CurrentRunCount.ShouldBe(5);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_Execute_Delayed_Tasks_Correctly()
    {
        // Arrange
        _host = CreateHost(useShardedScheduler: true, shardCount: 4);
        await _host.StartAsync();

        // Act - Schedule delayed task
        var task = new TestTaskConcurrent1();
        var taskId = await _dispatcher.Dispatch(task, TimeSpan.FromSeconds(1.2));

        // Assert - Wait for task to be in waiting queue
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for task to complete after delay
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_Handle_Multiple_Recurring_Tasks()
    {
        // Arrange
        _host = CreateHost(useShardedScheduler: true, shardCount: 8);
        await _host.StartAsync();

        // Act - Schedule 3 recurring tasks (they will be distributed across shards)
        var taskIds = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var taskId = await _dispatcher.Dispatch(
                new TestTaskRecurringSeconds(),
                recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(3)
            );
            taskIds.Add(taskId);
        }

        // Assert - Wait for all tasks to complete all 3 runs
        await TaskWaitHelper.WaitUntilAsync(
            async () => await _storage.GetAll(),
            tasks => tasks.Length == 3 && tasks.All(t => t.CurrentRunCount == 3 && t.Status == QueuedTaskStatus.Completed),
            timeoutMs: 20000 // Increased for .NET 6 compatibility (recurring tasks need more time)
        );

        var allTasks = await _storage.GetAll();
        allTasks.Length.ShouldBe(3);
        allTasks.ShouldAllBe(t => t.CurrentRunCount == 3);
        allTasks.ShouldAllBe(t => t.Status == QueuedTaskStatus.Completed);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_Compare_Default_Scheduler_Compatibility()
    {
        // Test 1: With default scheduler
        _host = CreateHost(useShardedScheduler: false);
        await _host.StartAsync();

        var taskId1 = await _dispatcher.Dispatch(
            new TestTaskConcurrent1(),
            DateTimeOffset.UtcNow.AddSeconds(1)
        );

        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId1, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var tasks1 = await _storage.GetAll();
        tasks1[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        var cts1 = new CancellationTokenSource();
        cts1.CancelAfter(2000);
        await _host.StopAsync(cts1.Token);

        // Test 2: With sharded scheduler (same behavior expected)
        _host = CreateHost(useShardedScheduler: true, shardCount: 4);
        await _host.StartAsync();

        var taskId2 = await _dispatcher.Dispatch(
            new TestTaskConcurrent2(),
            DateTimeOffset.UtcNow.AddSeconds(1)
        );

        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId2, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var tasks2 = await _storage.GetAll();
        // Find the task by ID (since we have tasks from previous test in storage if using same instance)
        var task2 = tasks2.FirstOrDefault(t => t.Id == taskId2);
        task2.ShouldNotBeNull();
        task2.Status.ShouldBe(QueuedTaskStatus.Completed);

        var cts2 = new CancellationTokenSource();
        cts2.CancelAfter(2000);
        await _host.StopAsync(cts2.Token);
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Scheduling_Operations()
    {
        // Arrange
        _host = CreateHost(useShardedScheduler: true, shardCount: 8);
        await _host.StartAsync();

        // Act - Concurrently schedule 50 tasks from multiple threads
        var schedulingTasks = Enumerable.Range(0, 50).Select(async i =>
        {
            return await _dispatcher.Dispatch(
                new TestTaskConcurrent1(),
                DateTimeOffset.UtcNow.AddSeconds(1 + (i % 5))
            );
        }).ToList();

        var taskIds = await Task.WhenAll(schedulingTasks);

        // Assert - All tasks should be scheduled
        taskIds.Length.ShouldBe(50);
        taskIds.Distinct().Count().ShouldBe(50); // All unique IDs

        // Wait for all 50 tasks to complete (verify via isolated storage, not shared state manager)
        await TaskWaitHelper.WaitUntilAsync(
            async () => await _storage.GetAll(),
            tasks => tasks.Count(t => taskIds.Contains(t.Id) && t.Status == QueuedTaskStatus.Completed) >= 50,
            timeoutMs: 15000
        );

        // Verify all 50 tasks completed successfully
        var completedTasks = await _storage.GetAll();
        completedTasks.Length.ShouldBe(50);
        completedTasks.ShouldAllBe(t => t.Status == QueuedTaskStatus.Completed);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_Route_Tasks_To_Custom_Queue_Name()
    {
        // Arrange - Create host with custom queue configuration
        var host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging();

                services.AddEverTask(cfg =>
                    {
                        cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                           .UseShardedScheduler(shardCount: 4)
                           .SetMaxDegreeOfParallelism(8);
                    })
                    .AddQueue("high-priority", q => q.SetMaxDegreeOfParallelism(4))
                    .AddMemoryStorage();

                services.AddSingleton<TestTaskStateManager>();
            })
            .Build();

        await host.StartAsync();

        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = host.Services.GetRequiredService<ITaskStorage>();
        var stateManager = host.Services.GetRequiredService<TestTaskStateManager>();
        var queueManager = host.Services.GetRequiredService<IWorkerQueueManager>();

        // Act - Schedule task with custom queue name
        var taskId = await dispatcher.Dispatch(
            new TestTaskHighPriority(), // This task specifies QueueName = "high-priority" in handler
            DateTimeOffset.UtcNow.AddSeconds(1)
        );

        // Wait for task to be processed
        await TaskWaitHelper.WaitForTaskStatusAsync(storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

        // Assert
        var tasks = await storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);
        task.ShouldNotBeNull();
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.QueueName.ShouldBe("high-priority", "Task should be routed to custom queue");

        // Verify the queue exists and processed the task
        var highPriorityQueue = queueManager.GetQueue("high-priority");
        highPriorityQueue.ShouldNotBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await host.StopAsync(cts.Token);
    }

    private IHost CreateHost(bool useShardedScheduler, int shardCount = 8)
    {
        return new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging();

                var config = services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                       .SetChannelOptions(100)
                       .SetMaxDegreeOfParallelism(16);

                    if (useShardedScheduler)
                        cfg.UseShardedScheduler(shardCount);
                });

                config.AddMemoryStorage();
                services.AddSingleton<TestTaskStateManager>();
            })
            .Build()
            .Tap(host =>
            {
                _dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
                _storage = host.Services.GetRequiredService<ITaskStorage>();
                _stateManager = host.Services.GetRequiredService<TestTaskStateManager>();
            });
    }
}

// Extension method for fluent initialization
internal static class HostExtensions
{
    internal static T Tap<T>(this T obj, Action<T> action)
    {
        action(obj);
        return obj;
    }
}

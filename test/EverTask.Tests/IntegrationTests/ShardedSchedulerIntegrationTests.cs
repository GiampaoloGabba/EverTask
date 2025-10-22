using EverTask.Scheduler;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

public class ShardedSchedulerIntegrationTests : IsolatedIntegrationTestBase
{

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
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 8));

        // Act - Schedule 1000 tasks rapidly (simula spike)
        var scheduleTasks = new List<Task<Guid>>();
        for (int i = 0; i < 1000; i++)
        {
            scheduleTasks.Add(
                Dispatcher.Dispatch(
                    new TestTaskConcurrent1(),
                    DateTimeOffset.UtcNow.AddSeconds(1 + (i % 60))
                )
            );
        }

        // Assert - All Schedule() calls must complete without exceptions
        await Task.WhenAll(scheduleTasks);

        // Verify persistence
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1000);
    }

    [Fact]
    public async Task Should_Process_Scheduled_Tasks_From_All_Shards()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 4));

        // Act - Schedule 20 tasks with execution time spread across 1-2 seconds
        var taskIds = new List<Guid>();
        for (int i = 0; i < 20; i++)
        {
            var taskId = await Dispatcher.Dispatch(
                new TestTaskConcurrent1(),
                DateTimeOffset.UtcNow.AddSeconds(1 + (i % 2))
            );
            taskIds.Add(taskId);
        }

        // Wait for all tasks to be executed (verify via isolated storage, not shared state manager)
        await TaskWaitHelper.WaitUntilAsync(
            async () => await Storage.GetAll(),
            tasks => tasks.Count(t => taskIds.Contains(t.Id) && t.Status == QueuedTaskStatus.Completed) >= 20,
            timeoutMs: 10000
        );

        // Assert - Verify exactly 20 tasks in THIS test's storage
        var completedTasks = await Storage.GetAll();
        completedTasks.Length.ShouldBe(20);
        completedTasks.ShouldAllBe(t => t.Status == QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_Handle_Recurring_Tasks_With_Shards()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 8));

        // Act - Schedule recurring task
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(5)
        );

        // Assert - Wait for 5 executions
        await TaskWaitHelper.WaitForRecurringRunsAsync(Storage, taskId, expectedRuns: 5, timeoutMs: 15000);

        var tasks = await Storage.GetAll();
        tasks[0].CurrentRunCount.ShouldBe(5);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_Execute_Delayed_Tasks_Correctly()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 4));

        // Act - Schedule delayed task
        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task, TimeSpan.FromSeconds(1.2));

        // Assert - Wait for task to be in waiting queue
        await TaskWaitHelper.WaitForTaskStatusAsync(Storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await Storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for task to complete after delay
        await TaskWaitHelper.WaitForTaskStatusAsync(Storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Handle_Multiple_Recurring_Tasks()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 8));

        // Act - Schedule 3 recurring tasks (they will be distributed across shards)
        var taskIds = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var taskId = await Dispatcher.Dispatch(
                new TestTaskRecurringSeconds(),
                recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(3)
            );
            taskIds.Add(taskId);
        }

        // Assert - Wait for all tasks to complete all 3 runs
        await TaskWaitHelper.WaitUntilAsync(
            async () => await Storage.GetAll(),
            tasks => tasks.Length == 3 && tasks.All(t => t.CurrentRunCount == 3 && t.Status == QueuedTaskStatus.Completed),
            timeoutMs: 20000 // Increased for .NET 6 compatibility (recurring tasks need more time)
        );

        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(3);
        allTasks.ShouldAllBe(t => t.CurrentRunCount == 3);
        allTasks.ShouldAllBe(t => t.Status == QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_Compare_Default_Scheduler_Compatibility()
    {
        // Test 1: With default scheduler
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16);

        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskConcurrent1(),
            DateTimeOffset.UtcNow.AddSeconds(1)
        );

        await TaskWaitHelper.WaitForTaskStatusAsync(Storage, taskId1, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var tasks1 = await Storage.GetAll();
        tasks1[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        await StopHostAsync();

        // Test 2: With sharded scheduler (same behavior expected) - Create new isolated host
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 4));

        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskConcurrent2(),
            DateTimeOffset.UtcNow.AddSeconds(1)
        );

        await TaskWaitHelper.WaitForTaskStatusAsync(Storage, taskId2, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var tasks2 = await Storage.GetAll();
        // This test's storage only has tasks2 (isolated from test1)
        tasks2.Length.ShouldBe(1);
        tasks2[0].Id.ShouldBe(taskId2);
        tasks2[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Scheduling_Operations()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostAsync(
            channelCapacity: 100,
            maxDegreeOfParallelism: 16,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 8));

        // Act - Concurrently schedule 50 tasks from multiple threads
        var schedulingTasks = Enumerable.Range(0, 50).Select(async i =>
        {
            return await Dispatcher.Dispatch(
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
            async () => await Storage.GetAll(),
            tasks => tasks.Count(t => taskIds.Contains(t.Id) && t.Status == QueuedTaskStatus.Completed) >= 50,
            timeoutMs: 15000
        );

        // Verify all 50 tasks completed successfully
        var completedTasks = await Storage.GetAll();
        completedTasks.Length.ShouldBe(50);
        completedTasks.ShouldAllBe(t => t.Status == QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_Route_Tasks_To_Custom_Queue_Name()
    {
        // Arrange: Create isolated host with custom queue and sharded scheduler
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder
                .ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(8))
                .AddQueue("high-priority", q => q.SetMaxDegreeOfParallelism(4))
                .AddMemoryStorage(),
            startHost: true,
            configureEverTask: cfg => cfg.UseShardedScheduler(shardCount: 4));

        var queueManager = Host!.Services.GetRequiredService<IWorkerQueueManager>();

        // Act - Schedule task with custom queue name
        var taskId = await Dispatcher.Dispatch(
            new TestTaskHighPriority(), // This task specifies QueueName = "high-priority" in handler
            DateTimeOffset.UtcNow.AddSeconds(1)
        );

        // Wait for task to be processed
        await TaskWaitHelper.WaitForTaskStatusAsync(Storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

        // Assert
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);
        task.ShouldNotBeNull();
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.QueueName.ShouldBe("high-priority", "Task should be routed to custom queue");

        // Verify the queue exists and processed the task
        var highPriorityQueue = queueManager.GetQueue("high-priority");
        highPriorityQueue.ShouldNotBeNull();
    }
}

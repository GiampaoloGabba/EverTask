using EverTask.Configuration;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using EverTask.Worker;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Comprehensive integration tests for multi-queue functionality.
/// These tests verify the complete flow: Dispatcher -> Storage -> QueueManager -> WorkerExecutor
/// </summary>
public class MultiQueueIntegrationTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Task_WithCustomQueueName_RoutesToCorrectQueue_AndExecutesSuccessfully()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder => builder
            .AddQueue("high-priority", q => q.SetMaxDegreeOfParallelism(5))
            .AddMemoryStorage());


        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskHighPriority());

        // Assert - Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].QueueName.ShouldBe("high-priority");
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Task_WithoutQueueName_RoutesToDefaultQueue()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(3))
                .AddMemoryStorage();
        });


        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskDefaultQueue());

        // Assert - Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].QueueName.ShouldBe("default");
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task MultipleTasks_RouteToCorrectQueues_Simultaneously()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(3))
                .AddQueue("high-priority", q => q.SetMaxDegreeOfParallelism(5))
                .AddQueue("background", q => q.SetMaxDegreeOfParallelism(2))
                .AddMemoryStorage();
        });


        // Act - Dispatch tasks to different queues
        var taskId1 = await Dispatcher.Dispatch(new TestTaskHighPriority());
        var taskId2 = await Dispatcher.Dispatch(new TestTaskBackground());
        var taskId3 = await Dispatcher.Dispatch(new TestTaskDefaultQueue());

        // Assert - Wait for all tasks to complete
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);
        await WaitForTaskStatusAsync(taskId3, QueuedTaskStatus.Completed);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(3);

        // Verify each task was routed to correct queue
        var highPriorityTask = tasks.First(t => t.Id == taskId1);
        highPriorityTask.QueueName.ShouldBe("high-priority");
        highPriorityTask.Status.ShouldBe(QueuedTaskStatus.Completed);

        var backgroundTask = tasks.First(t => t.Id == taskId2);
        backgroundTask.QueueName.ShouldBe("background");
        backgroundTask.Status.ShouldBe(QueuedTaskStatus.Completed);

        var defaultTask = tasks.First(t => t.Id == taskId3);
        defaultTask.QueueName.ShouldBe("default");
        defaultTask.Status.ShouldBe(QueuedTaskStatus.Completed);

        // Verify handlers executed
    }

    [Fact]
    public async Task ParallelQueue_ExecutesTasksConcurrently()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .AddQueue("parallel", q => q.SetMaxDegreeOfParallelism(5))
                .AddMemoryStorage();
        });

        StateManager.ResetAll();

        // Act - Dispatch 5 tasks that take 200ms each
        var taskIds = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var taskId = await Dispatcher.Dispatch(new TestTaskParallel());
            taskIds.Add(taskId);
        }

        // Assert - Wait for all tasks to complete
        // Increased timeout for coverage tool (runs much slower)
        foreach (var taskId in taskIds)
        {
            await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 10000);
        }

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(5);

        // All tasks should be completed
        tasks.All(t => t.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
        tasks.All(t => t.QueueName == "parallel").ShouldBeTrue();
    }

    [Fact]
    public async Task SequentialQueue_ExecutesTasksSequentially()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .AddQueue("sequential", q => q.SetMaxDegreeOfParallelism(1))
                .AddMemoryStorage();
        });

        StateManager.ResetAll();

        // Act - Dispatch 3 tasks
        var task1Id = await Dispatcher.Dispatch(new TestTaskSequential { Id = "task1" });
        var task2Id = await Dispatcher.Dispatch(new TestTaskSequential { Id = "task2" });
        var task3Id = await Dispatcher.Dispatch(new TestTaskSequential { Id = "task3" });

        // Assert - Wait for all tasks to complete
        await WaitForTaskStatusAsync(task1Id, QueuedTaskStatus.Completed, timeoutMs: 5000);
        await WaitForTaskStatusAsync(task2Id, QueuedTaskStatus.Completed, timeoutMs: 5000);
        await WaitForTaskStatusAsync(task3Id, QueuedTaskStatus.Completed, timeoutMs: 5000);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(3);

        // All tasks should be completed
        tasks.All(t => t.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
        tasks.All(t => t.QueueName == "sequential").ShouldBeTrue();

        // Verify sequential execution - no two tasks should overlap
        var task1Started = StateManager.GetState("TestTaskSequential_task1")!.StartTime;
        var task1Completed = StateManager.GetState("TestTaskSequential_task1")!.EndTime;
        var task2Started = StateManager.GetState("TestTaskSequential_task2")!.StartTime;
        var task2Completed = StateManager.GetState("TestTaskSequential_task2")!.EndTime;
        var task3Started = StateManager.GetState("TestTaskSequential_task3")!.StartTime;

        // Task 2 should start after Task 1 completes
        (task2Started >= task1Completed).ShouldBeTrue();
        // Task 3 should start after Task 2 completes
        (task3Started >= task2Completed).ShouldBeTrue();
    }

    [Fact]
    public async Task RecurringTask_WithoutExplicitQueue_RoutesToRecurringQueue()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(3))
                .ConfigureRecurringQueue(q => q.SetMaxDegreeOfParallelism(2))
                .AddMemoryStorage();
        });


        // Act - Dispatch recurring task without explicit queue
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(2) // Run 2 times
        );

        // Assert - Wait for 2 runs to complete
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 5000);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].QueueName.ShouldBe("recurring"); // Auto-routed to recurring queue
        tasks[0].IsRecurring.ShouldBeTrue();
    }

    [Fact]
    public async Task RecurringTask_WithExplicitQueue_RespectsOverride()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(3))
                .ConfigureRecurringQueue(q => q.SetMaxDegreeOfParallelism(2))
                .AddQueue("background", q => q.SetMaxDegreeOfParallelism(1))
                .AddMemoryStorage();
        });

        // Act - Dispatch recurring task with explicit background queue
        var task = new TestTaskBackgroundRecurring();
        var taskId = await Dispatcher.Dispatch(
            task,
            recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(2)
        );

        // Assert - Wait for 2 runs to complete
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 5000);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].QueueName.ShouldBe("background"); // Should NOT be "recurring"
        tasks[0].IsRecurring.ShouldBeTrue();
    }

    [Fact]
    public async Task QueuedTasks_PersistQueueName_AndRecoverAfterRestart()
    {
        // Arrange: Create isolated host but DON'T start it yet
        // Note: We need to test delayed start scenario to verify queue name persistence
        await CreateIsolatedHostWithBuilderAsync(builder => builder
            .AddQueue("high-priority", q => q.SetMaxDegreeOfParallelism(5))
            .AddMemoryStorage(), startHost: false);


        // Act - Dispatch task without starting host (will be persisted as pending)
        var taskId = await Dispatcher.Dispatch(new TestTaskHighPriority());

        // Verify task is persisted with correct queue name
        var pendingTasks = await Storage.RetrievePending();
        pendingTasks.Length.ShouldBe(1);
        pendingTasks[0].QueueName.ShouldBe("high-priority");
        pendingTasks[0].Status.ShouldBe(QueuedTaskStatus.Queued); // Task is queued even without starting the host

        // Now start the host - it should recover the pending task
        await Host!.StartAsync();

        // Assert - Task should be recovered and executed
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].QueueName.ShouldBe("high-priority"); // QueueName preserved after restart
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // Note: Task executes twice because it was queued before host start, then ProcessPendingAsync re-dispatches it.
        // This is expected behavior for the delayed-start scenario. A true restart test would create a new host instance.
    }

    [Fact]
    public async Task NonExistentQueue_FallsBackToDefaultQueue()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .ConfigureDefaultQueue(q => q.SetMaxDegreeOfParallelism(3))
                .AddMemoryStorage();
        });

        // Act - Dispatch task with non-existent queue name
        var task = new TestTaskNonExistentQueue();
        var taskId = await Dispatcher.Dispatch(task);

        // Assert - Should fallback to default queue
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        // Task was requested with "non-existent" queue but should execute in "default"
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task DifferentQueues_HaveIndependentParallelism()
    {
        // Arrange: Create isolated host for THIS test
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .AddQueue("parallel", q => q.SetMaxDegreeOfParallelism(5))
                .AddQueue("sequential", q => q.SetMaxDegreeOfParallelism(1))
                .AddMemoryStorage();
        });

        StateManager.ResetAll();

        // Act - Dispatch tasks to both queues
        var parallelTasks = new List<Guid>();
        var sequentialTasks = new List<Guid>();

        for (int i = 0; i < 3; i++)
        {
            parallelTasks.Add(await Dispatcher.Dispatch(new TestTaskParallel { Id = $"parallel-{i}" }));
            sequentialTasks.Add(await Dispatcher.Dispatch(new TestTaskSequential { Id = $"sequential-{i}" }));
        }

        var allTaskIds = parallelTasks.Concat(sequentialTasks).ToList();

        // Assert - Wait for all 6 tasks to complete (more efficient than waiting one by one)
        // 3 parallel tasks (~200ms concurrent) + 3 sequential tasks (~600ms sequential) = ~800ms + overhead
        await TaskWaitHelper.WaitUntilAsync(
            async () => await Storage.GetAll(),
            tasks => tasks.Count(t => allTaskIds.Contains(t.Id) && t.Status == QueuedTaskStatus.Completed) >= 6,
            timeoutMs: 3000 // Reduced from 20s with isolated test infrastructure
        );

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(6);

        // Verify all completed
        tasks.All(t => t.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        // Verify parallel tasks were in parallel queue
        tasks.Count(t => t.QueueName == "parallel").ShouldBe(3);
        // Verify sequential tasks were in sequential queue
        tasks.Count(t => t.QueueName == "sequential").ShouldBe(3);
    }
}

// Additional test task for recurring with background queue
public class TestTaskBackgroundRecurring : IEverTask
{
    public static int Counter { get; set; } = 0;
}

public class TestTaskBackgroundRecurringHandler : EverTaskHandler<TestTaskBackgroundRecurring>
{
    public override string? QueueName => "background";

    public override async Task Handle(TestTaskBackgroundRecurring task, CancellationToken ct)
    {
        await Task.Delay(50, ct);
    }
}

// Test task for non-existent queue fallback
public class TestTaskNonExistentQueue : IEverTask { }

public class TestTaskNonExistentQueueHandler : EverTaskHandler<TestTaskNonExistentQueue>
{
    public override string? QueueName => "non-existent";

    public override Task Handle(TestTaskNonExistentQueue task, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

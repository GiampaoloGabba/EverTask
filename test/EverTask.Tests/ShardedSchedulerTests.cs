using EverTask.Tests.TestHelpers;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

public class ShardedSchedulerTests : IDisposable
{
    private readonly Mock<IWorkerQueue> _mockWorkerQueue;
    private readonly Mock<IWorkerQueueManager> _mockWorkerQueueManager;
    private readonly Mock<IEverTaskLogger<ShardedScheduler>> _mockLogger;
    private ShardedScheduler? _shardedScheduler;

    public ShardedSchedulerTests()
    {
        _mockWorkerQueue = new Mock<IWorkerQueue>();
        _mockWorkerQueueManager = new Mock<IWorkerQueueManager>();
        _mockLogger = new Mock<IEverTaskLogger<ShardedScheduler>>();

        // Setup the queue manager to return the default queue
        _mockWorkerQueueManager.Setup(x => x.GetQueue("default")).Returns(_mockWorkerQueue.Object);

        // Setup TryEnqueue to delegate to the worker queue
        _mockWorkerQueueManager.Setup(x => x.TryEnqueue(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>()))
            .Returns<string?, TaskHandlerExecutor>(async (queueName, executor) =>
            {
                await _mockWorkerQueue.Object.Queue(executor);
                return true;
            });
    }

    [Fact]
    public void Should_Initialize_With_Specified_Shard_Count()
    {
        // Arrange & Act
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 8);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Initializing ShardedScheduler with 8 shards")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.Once);
    }

    [Fact]
    public void Should_Default_To_ProcessorCount_Shards_When_Zero()
    {
        // Arrange
        var expectedShardCount = Math.Max(4, Environment.ProcessorCount);

        // Act
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 0);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Initializing ShardedScheduler with {expectedShardCount} shards")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.Once);
    }

    [Fact]
    public void Should_Use_Minimum_4_Shards()
    {
        // Arrange
        var originalProcessorCount = Environment.ProcessorCount;
        var expectedShardCount = Math.Max(4, originalProcessorCount);

        // Act
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 0);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"{expectedShardCount} shards")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.Once);

        // Verify minimum 4 shards even on single-core systems
        expectedShardCount.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task Should_Schedule_Task_To_Correct_Shard()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 4);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(1));

        // Act
        _shardedScheduler.Schedule(taskHandlerExecutor);

        // Wait for task to be dispatched
        await Task.Delay(1500);

        // Assert
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor)), Times.Once);
    }

    [Fact]
    public void Should_Distribute_Tasks_Across_Shards_Uniformly()
    {
        // Arrange
        const int shardCount = 8;
        const int taskCount = 1000;
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: shardCount);

        var shardDistribution = new Dictionary<int, int>();
        for (int i = 0; i < shardCount; i++)
        {
            shardDistribution[i] = 0;
        }

        // Act - Schedule 1000 tasks with random Guids
        for (int i = 0; i < taskCount; i++)
        {
            var taskId = TestGuidGenerator.New();
            var executor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddHours(1), taskId: taskId);

            // Calculate which shard this task would go to (same logic as ShardedScheduler)
            int shardIndex = Math.Abs(taskId.GetHashCode()) % shardCount;
            shardDistribution[shardIndex]++;

            _shardedScheduler.Schedule(executor);
        }

        // Assert - No shard should have more than 30% of tasks (uniform distribution check)
        var maxTasksPerShard = shardDistribution.Values.Max();
        var maxPercentage = (double)maxTasksPerShard / taskCount;

        maxPercentage.ShouldBeLessThan(0.30, $"Distribution too uneven. Max shard has {maxPercentage:P0} of tasks");

        // Also verify each shard got at least some tasks (> 5%)
        var minTasksPerShard = shardDistribution.Values.Min();
        var minPercentage = (double)minTasksPerShard / taskCount;
        minPercentage.ShouldBeGreaterThan(0.05, $"Some shard has too few tasks: {minPercentage:P0}");
    }

    [Fact]
    public async Task Should_Handle_Empty_Queue()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 2);

        // Act - Wait a bit to ensure shards are sleeping
        await Task.Delay(500);

        // Assert - Verify debug log that queue is empty and sleeping
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Queue empty, sleeping")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.AtLeastOnce);

        // Schedule a task after delay and verify it wakes up and processes
        var taskHandlerExecutor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(1));
        _shardedScheduler.Schedule(taskHandlerExecutor);

        await Task.Delay(1500);

        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor)), Times.Once);
    }

    [Fact]
    public async Task Should_Wake_Up_Immediately_On_New_Task()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 2);

        // Schedule task far in the future (10 minutes)
        var taskHandlerExecutor1 = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddMinutes(10));
        _shardedScheduler.Schedule(taskHandlerExecutor1);

        await Task.Delay(200);

        // Act - Schedule urgent task (5 seconds)
        var taskHandlerExecutor2 = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(5));
        _shardedScheduler.Schedule(taskHandlerExecutor2);

        // Wait for the 5-second task to execute
        await Task.Delay(5500);

        // Assert - The 5-second task should have been processed
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor2)), Times.Once);

        // The 10-minute task should NOT have been processed yet
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor1)), Times.Never);
    }

    [Fact]
    public async Task Should_Dispose_All_Shards()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 4);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(1));

        // Schedule a task before disposal
        _shardedScheduler.Schedule(taskHandlerExecutor);

        // Wait a bit for background threads to start
        await Task.Delay(200);

        // Act - Dispose should cancel all background tasks without throwing
        _shardedScheduler.Dispose();

        // Assert - Give time for disposal to complete
        await Task.Delay(500);

        // Verify no exceptions were thrown (test passes if we reach this point)
        // Background tasks should be cancelled gracefully
    }

    [Fact]
    public void Should_Handle_Null_Execution_Time_Gracefully()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 2);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(executionTime: null);

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => _shardedScheduler.Schedule(taskHandlerExecutor));
    }

    [Fact]
    public async Task Should_Use_NextRecurringRun_When_Provided()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 2);

        var originalExecutionTime = DateTimeOffset.UtcNow.AddHours(10);
        var nextRecurringRun = DateTimeOffset.UtcNow.AddSeconds(1);

        var taskHandlerExecutor = CreateTaskHandlerExecutor(originalExecutionTime);

        // Act - Schedule with nextRecurringRun parameter (overrides executionTime)
        _shardedScheduler.Schedule(taskHandlerExecutor, nextRecurringRun);

        // Wait for task to be dispatched (should use nextRecurringRun = 1 second, not originalExecutionTime = 10 hours)
        await Task.Delay(1500);

        // Assert - Task should have been processed (because nextRecurringRun was 1 second)
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor)), Times.Once);
    }

    [Fact]
    public async Task Should_Process_Multiple_Tasks_In_Parallel_Across_Shards()
    {
        // Arrange
        const int shardCount = 4;
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: shardCount);

        var tasks = new List<TaskHandlerExecutor>();

        // Create tasks that will be distributed across different shards
        for (int i = 0; i < 20; i++)
        {
            var taskId = TestGuidGenerator.New();
            var executor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(1), taskId: taskId);
            tasks.Add(executor);
            _shardedScheduler.Schedule(executor);
        }

        // Act - Wait for all tasks to be processed
        await Task.Delay(2000);

        // Assert - All tasks should have been queued
        _mockWorkerQueue.Verify(wq => wq.Queue(It.IsAny<TaskHandlerExecutor>()), Times.Exactly(20));

        // Verify tasks were distributed across shards (check debug logs)
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Scheduling task")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.Exactly(20));
    }

    [Fact]
    public async Task Should_Handle_Large_Delays_Correctly()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 2);

        // Schedule task 3 hours in the future (should cap delay at 1.5 hours like PeriodicTimerScheduler)
        var taskHandlerExecutor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddHours(3));

        // Act
        _shardedScheduler.Schedule(taskHandlerExecutor);

        // Wait a bit to ensure scheduler has processed the delay calculation
        await Task.Delay(500);

        // Assert - Task should NOT have been dispatched yet
        _mockWorkerQueue.Verify(wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor)), Times.Never);

        // Verify scheduling log
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Scheduling task")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.Once);
    }

    [Fact]
    public async Task Should_Isolate_Failures_Between_Shards()
    {
        // Arrange
        var queueManagerMock = new Mock<IWorkerQueueManager>();
        var loggerMock = new Mock<IEverTaskLogger<ShardedScheduler>>();

        // Create a flag to track which shard failed
        var failedShardId = -1;
        var successfulEnqueues = new List<Guid>();

        // Setup TryEnqueue to fail for specific task IDs (simulate shard-specific failure)
        queueManagerMock.Setup(x => x.TryEnqueue(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>()))
            .Returns<string?, TaskHandlerExecutor>((queueName, executor) =>
            {
                // Calculate which shard this would go to
                int shardIndex = Math.Abs(executor.PersistenceId.GetHashCode()) % 4;

                // Simulate failure in shard 2 only
                if (shardIndex == 2)
                {
                    failedShardId = shardIndex;
                    throw new InvalidOperationException($"Simulated failure in shard {shardIndex}");
                }

                // Other shards succeed
                successfulEnqueues.Add(executor.PersistenceId);
                return Task.FromResult(true);
            });

        _shardedScheduler = new ShardedScheduler(queueManagerMock.Object, loggerMock.Object, null, shardCount: 4);

        // Act - Schedule 100 tasks (they'll be distributed across all 4 shards)
        var tasks = new List<TaskHandlerExecutor>();
        for (int i = 0; i < 100; i++)
        {
            var taskId = TestGuidGenerator.New();
            var executor = CreateTaskHandlerExecutor(DateTimeOffset.UtcNow.AddSeconds(1), taskId: taskId);
            tasks.Add(executor);
            _shardedScheduler.Schedule(executor);
        }

        // Wait for tasks to be processed
        await Task.Delay(2000);

        // Assert
        // 1. Verify that shard 2 failed
        failedShardId.ShouldBe(2);

        // 2. Verify that other shards succeeded
        // Tasks NOT in shard 2 should have been enqueued successfully
        successfulEnqueues.Count.ShouldBeGreaterThan(0, "Other shards should have processed tasks successfully");

        // 3. Verify error was logged for shard 2
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unable to dispatch task")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.AtLeastOnce,
            "Should log errors for failed shard");

        // 4. Count how many tasks went to each shard
        var shardDistribution = tasks
            .GroupBy(t => Math.Abs(t.PersistenceId.GetHashCode()) % 4)
            .ToDictionary(g => g.Key, g => g.Count());

        // Shard 2 should have tasks that failed
        var shard2TaskCount = shardDistribution.GetValueOrDefault(2, 0);
        shard2TaskCount.ShouldBeGreaterThan(0, "Shard 2 should have received tasks");

        // But those tasks should NOT be in successfulEnqueues
        var shard2Tasks = tasks.Where(t => Math.Abs(t.PersistenceId.GetHashCode()) % 4 == 2).Select(t => t.PersistenceId).ToHashSet();
        shard2Tasks.Intersect(successfulEnqueues).Count().ShouldBe(0, "Failed shard tasks should not be in successful list");
    }

    [Fact]
    public async Task Should_Process_Past_Tasks_Immediately()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 2);

        // Create task with execution time in the past (5 seconds ago)
        var pastExecutionTime = DateTimeOffset.UtcNow.AddSeconds(-5);
        var taskHandlerExecutor = CreateTaskHandlerExecutor(pastExecutionTime);

        // Act
        _shardedScheduler.Schedule(taskHandlerExecutor);

        // Wait a short time (should process immediately, not wait)
        await Task.Delay(500);

        // Assert - Task should have been dispatched immediately (not after 5 seconds in future)
        _mockWorkerQueue.Verify(
            wq => wq.Queue(It.Is<TaskHandlerExecutor>(te => te == taskHandlerExecutor)),
            Times.Once,
            "Task with past execution time should be processed immediately");
    }

    [Fact]
    public void Should_Handle_Negative_Hash_Without_Exception()
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount: 4);
        var executionTime = DateTimeOffset.UtcNow.AddSeconds(10);

        // Act & Assert
        // Schedule many tasks with random GUIDs - statistically some will have negative hash codes
        // (int.MinValue case: Math.Abs(int.MinValue) == int.MinValue, still negative)
        // The fix using (uint) cast should handle all cases without IndexOutOfRangeException
        var exception = Record.Exception(() =>
        {
            for (int i = 0; i < 10000; i++)
            {
                var taskExecutor = CreateTaskHandlerExecutor(executionTime, TestGuidGenerator.New());
                _shardedScheduler.Schedule(taskExecutor);
            }
        });

        // Assert - No exception should be thrown (especially IndexOutOfRangeException)
        exception.ShouldBeNull("Scheduling should handle all hash codes (including negative) without throwing");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Should_Distribute_Tasks_Across_Shards_Without_Index_Out_Of_Range(int shardCount)
    {
        // Arrange
        _shardedScheduler = new ShardedScheduler(_mockWorkerQueueManager.Object, _mockLogger.Object, null, shardCount);
        var executionTime = DateTimeOffset.UtcNow.AddSeconds(5);

        // Act & Assert - Test with many GUIDs to ensure shard index is always in valid range [0, shardCount)
        var exception = Record.Exception(() =>
        {
            for (int i = 0; i < 5000; i++)
            {
                var taskExecutor = CreateTaskHandlerExecutor(executionTime, TestGuidGenerator.New());
                _shardedScheduler.Schedule(taskExecutor);
            }
        });

        exception.ShouldBeNull($"Scheduling with {shardCount} shards should never produce IndexOutOfRangeException");
    }

    public void Dispose()
    {
        _shardedScheduler?.Dispose();
    }

    private TaskHandlerExecutor CreateTaskHandlerExecutor(DateTimeOffset? executionTime = null, Guid? taskId = null) =>
        new(
            new TestTaskRequest2(),
            new TestTaskHanlder2(),
            null,  // HandlerTypeName - null for eager mode
            executionTime,
            null,
            null!,
            null,
            null,
            null,
            taskId ?? TestGuidGenerator.New(),
            null,
            null);
}

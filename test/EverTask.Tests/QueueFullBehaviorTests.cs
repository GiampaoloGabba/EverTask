using EverTask.Tests.TestHelpers;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Worker;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

/// <summary>
/// Tests for queue-full behavior policies (ThrowException, Wait, FallbackToDefault).
/// These tests verify the TryQueue method behavior when queues reach capacity.
/// </summary>
public class QueueFullBehaviorTests
{
    private TaskHandlerExecutor CreateTaskExecutor() =>
        new(
            new TestTaskRequest2(),
            new TestTaskHanlder2(),
            null,  // HandlerTypeName - null for eager mode
            null,
            null,
            null!,
            null,
            null,
            null,
            TestGuidGenerator.New(),
            null,
            null);

    [Fact]
    public async Task TryQueue_Should_Return_False_When_Queue_Is_Full()
    {
        // Arrange - Create queue with capacity 2
        var loggerMock = new Mock<ILogger>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();

        var queueConfig = new QueueConfiguration
        {
            Name = "test-queue",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(2)
        };

        var queue = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object);

        // Fill the queue
        var task1 = CreateTaskExecutor();
        var task2 = CreateTaskExecutor();
        var result1 = await queue.TryQueue(task1);
        var result2 = await queue.TryQueue(task2);

        result1.ShouldBeTrue();
        result2.ShouldBeTrue();

        // Act - Try to add third task
        var task3 = CreateTaskExecutor();
        var result3 = await queue.TryQueue(task3);

        // Assert - Should return false (queue full)
        result3.ShouldBeFalse("TryQueue should return false when queue is full");
    }

    [Fact]
    public async Task TryQueue_Should_Return_True_After_Space_Is_Available()
    {
        // Arrange - Create queue with capacity 2
        var loggerMock = new Mock<ILogger>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();

        var queueConfig = new QueueConfiguration
        {
            Name = "test-queue",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(2)
        };

        var queue = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object);

        // Fill the queue
        var task1 = CreateTaskExecutor();
        var task2 = CreateTaskExecutor();
        await queue.TryQueue(task1);
        await queue.TryQueue(task2);

        // Dequeue one task to make space
        await queue.Dequeue(CancellationToken.None);

        // Act - Try to add third task
        var task3 = CreateTaskExecutor();
        var result3 = await queue.TryQueue(task3);

        // Assert - Should return true (space available)
        result3.ShouldBeTrue("TryQueue should return true when space is available");
    }

    [Fact]
    public async Task Queue_Should_Wait_When_Queue_Is_Full()
    {
        // Arrange - Create queue with capacity 2
        var loggerMock = new Mock<ILogger>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();

        var queueConfig = new QueueConfiguration
        {
            Name = "test-queue",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(2)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
            }
        };

        var queue = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object);

        // Fill the queue to capacity
        var task1 = CreateTaskExecutor();
        var task2 = CreateTaskExecutor();
        await queue.Queue(task1);
        await queue.Queue(task2);

        // Act - Try to queue third task (should wait)
        var task3 = CreateTaskExecutor();
        var queueTask = Task.Run(async () =>
        {
            await queue.Queue(task3);
        });

        // Assert - Task should not complete immediately (queue is full)
        await Task.Delay(100);
        queueTask.IsCompleted.ShouldBeFalse("Queue should be waiting for space");

        // Dequeue one task to make space
        await queue.Dequeue(CancellationToken.None);

        // Now the queue should complete
        await Task.WhenAny(queueTask, Task.Delay(2000));
        queueTask.IsCompleted.ShouldBeTrue("Queue should complete after space is available");
    }

    [Fact]
    public async Task Queue_Should_Block_Until_Space_Available_With_Wait_FullMode()
    {
        // Arrange
        var loggerMock = new Mock<ILogger>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();

        var queueConfig = new QueueConfiguration
        {
            Name = "test-queue",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
            }
        };

        var queue = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object);

        // Fill the queue
        var task1 = CreateTaskExecutor();
        await queue.Queue(task1);

        // Act - Start queuing second task in background (should block)
        var task2 = CreateTaskExecutor();
        var queueTask = Task.Run(async () => await queue.Queue(task2));

        // Verify it's blocked
        await Task.Delay(100);
        queueTask.IsCompleted.ShouldBeFalse("Queue operation should block when full");

        // Dequeue to make space
        await queue.Dequeue(CancellationToken.None);

        // Verify queue operation completes
        var completedTask = await Task.WhenAny(queueTask, Task.Delay(2000));
        completedTask.ShouldBe(queueTask, "Queue operation should complete after dequeue");
    }

    [Fact]
    public async Task Queue_Should_Not_Accept_Blacklisted_Tasks()
    {
        // Arrange
        var loggerMock = new Mock<ILogger>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();

        var queueConfig = new QueueConfiguration
        {
            Name = "test-queue",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(10)
        };

        var queue = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object);

        var task = CreateTaskExecutor();

        // Setup blacklist to return true for this task
        mockBlacklist.Setup(x => x.IsBlacklisted(task.PersistenceId)).Returns(true);

        // Act
        var tryQueueResult = await queue.TryQueue(task);

        // Assert
        tryQueueResult.ShouldBeFalse("TryQueue should return false for blacklisted tasks");
    }

    [Fact]
    public async Task Queue_Should_Update_Storage_Status_To_Queued()
    {
        // Arrange
        var loggerMock = new Mock<ILogger>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var mockStorage = new Mock<ITaskStorage>();

        var queueConfig = new QueueConfiguration
        {
            Name = "test-queue",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(10)
        };

        var queue = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object, mockStorage.Object);

        var task = CreateTaskExecutor();

        // Act
        await queue.Queue(task);

        // Assert - Verify SetQueued was called
        mockStorage.Verify(x => x.SetQueued(task.PersistenceId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryQueue_Should_Not_Update_Storage_When_Queue_Is_Full()
    {
        // Arrange
        var loggerMock = new Mock<ILogger>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var mockStorage = new Mock<ITaskStorage>();

        var queueConfig = new QueueConfiguration
        {
            Name = "test-queue",
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(1)
        };

        var queue = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object, mockStorage.Object);

        // Fill the queue
        var task1 = CreateTaskExecutor();
        await queue.TryQueue(task1);

        // Reset mock to clear previous call
        mockStorage.Invocations.Clear();

        // Act - Try to add second task (should fail)
        var task2 = CreateTaskExecutor();
        var result = await queue.TryQueue(task2);

        // Assert
        result.ShouldBeFalse();
        mockStorage.Verify(x => x.SetQueued(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

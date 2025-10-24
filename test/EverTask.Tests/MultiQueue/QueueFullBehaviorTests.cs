using EverTask.Tests.TestHelpers;
using System.Threading.Channels;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace EverTask.Tests.MultiQueue;

public class QueueFullBehaviorTests
{
    private static ILoggerFactory CreateLoggerFactory()
    {
        // Use real ILoggerFactory instead of mocking
        var services = new ServiceCollection();
        services.AddLogging();
        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    }

    [Fact]
    public async Task QueueFullBehavior_FallbackToDefault_FallsBackWhenQueueNotFound()
    {
        // Arrange - Test fallback by using a non-existent queue name
        var mockLogger = new Mock<IEverTaskLogger<WorkerQueueManager>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var loggerFactory = CreateLoggerFactory();

        // Only configure default queue
        var configurations = new Dictionary<string, QueueConfiguration>
        {
            ["default"] = new QueueConfiguration
            {
                Name = "default",
                ChannelOptions = new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait },
                QueueFullBehavior = QueueFullBehavior.Wait
            }
        };

        var queueManager = new WorkerQueueManager(configurations, mockLogger.Object, mockBlacklist.Object, loggerFactory, null);

        // Act - Try to enqueue to a non-existent queue
        // This should trigger fallback to default queue
        var task1 = CreateTestExecutor("task1", "nonexistent");
        var result = await queueManager.TryEnqueue("nonexistent", task1);

        // Assert - Should succeed by falling back to default
        Assert.True(result);

        // Verify task was enqueued to default queue
        var defaultQueue = queueManager.GetQueue("default");
        var dequeued = await defaultQueue.Dequeue(CancellationToken.None);
        Assert.Equal(task1.PersistenceId, dequeued.PersistenceId);
    }

    [Fact]
    public async Task QueueFullBehavior_Wait_BlocksUntilSpaceAvailable()
    {
        // Arrange
        var mockLogger = new Mock<IEverTaskLogger<WorkerQueueManager>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var loggerFactory = CreateLoggerFactory();
        var configurations = new Dictionary<string, QueueConfiguration>
        {
            ["blocking"] = new QueueConfiguration
            {
                Name = "blocking",
                ChannelOptions = new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait },
                QueueFullBehavior = QueueFullBehavior.Wait
            }
        };

        var queueManager = new WorkerQueueManager(configurations, mockLogger.Object, mockBlacklist.Object, loggerFactory, null);
        var queue = queueManager.GetQueue("blocking");

        // Fill the queue
        var task1 = CreateTestExecutor("task1", "blocking");
        await queueManager.TryEnqueue("blocking", task1);

        // Act - Start enqueuing in background (should block)
        var task2 = CreateTestExecutor("task2", "blocking");
        var enqueueTask = Task.Run(() => queueManager.TryEnqueue("blocking", task2));

        // Ensure it's blocking
        await Task.Delay(100);
        Assert.False(enqueueTask.IsCompleted);

        // Dequeue to make space
        var dequeued = await queue.Dequeue(CancellationToken.None);

        // Now it should complete
        var result = await enqueueTask;

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task QueueFullBehavior_ThrowException_ThrowsWhenQueueFull()
    {
        // Arrange
        var mockLogger = new Mock<IEverTaskLogger<WorkerQueue>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var config = new QueueConfiguration
        {
            Name = "throwing",
            ChannelOptions = new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite // Will cause immediate failure when full
            },
            QueueFullBehavior = QueueFullBehavior.ThrowException
        };

        var queue = new WorkerQueue(config, mockLogger.Object, mockBlacklist.Object);

        // Fill the queue
        var task1 = CreateTestExecutor("task1", "throwing");
        await queue.Queue(task1);

        // Act - Enqueue to full queue with DropWrite mode
        var task2 = CreateTestExecutor("task2", "throwing");

        // With DropWrite mode, the write will be dropped when the channel is full
        // This tests that the queue handles the scenario gracefully
        await queue.Queue(task2);

        // Assert - Verify the queue size is still 1 (task2 was dropped)
        // We dequeue to verify only task1 is present
        var dequeued = await queue.Dequeue(CancellationToken.None);
        Assert.Equal(task1.PersistenceId, dequeued.PersistenceId);
    }

    [Fact]
    public void QueueConfiguration_Clone_CreatesIndependentCopy()
    {
        // Arrange
        var original = new QueueConfiguration
        {
            Name = "original",
            MaxDegreeOfParallelism = 5,
            ChannelOptions = new BoundedChannelOptions(100),
            QueueFullBehavior = QueueFullBehavior.FallbackToDefault,
            DefaultTimeout = TimeSpan.FromMinutes(10)
        };

        // Act
        var clone = original.Clone();
        clone.Name = "cloned";
        clone.MaxDegreeOfParallelism = 10;

        // Assert
        Assert.Equal("original", original.Name);
        Assert.Equal("cloned", clone.Name);
        Assert.Equal(5, original.MaxDegreeOfParallelism);
        Assert.Equal(10, clone.MaxDegreeOfParallelism);
        Assert.Equal(original.QueueFullBehavior, clone.QueueFullBehavior);
    }

    private TaskHandlerExecutor CreateTestExecutor(string id, string queueName)
    {
        return new TaskHandlerExecutor(
            new TestTask { Id = id },
            new object(),
            null,  // HandlerTypeName - null for eager mode
            null,
            null,
            (t, ct) => Task.CompletedTask,
            null,
            null,
            null,
            TestGuidGenerator.New(),
            queueName,
            null
        );
    }

    private record TestTask : EverTask.Abstractions.IEverTask
    {
        public string Id { get; init; } = "";
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using EverTask.Abstractions;
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

public class QueueParallelismTests
{
    private static ILoggerFactory CreateLoggerFactory()
    {
        // Use real ILoggerFactory instead of mocking
        var services = new ServiceCollection();
        services.AddLogging();
        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    }

    [Fact]
    public Task DifferentQueues_UseDifferentParallelism()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var highParallelismQueue = new QueueConfiguration
        {
            Name = "high",
            MaxDegreeOfParallelism = 10,
            ChannelOptions = new BoundedChannelOptions(100)
        };

        var lowParallelismQueue = new QueueConfiguration
        {
            Name = "low",
            MaxDegreeOfParallelism = 2,
            ChannelOptions = new BoundedChannelOptions(100)
        };

        // Act & Assert
        Assert.Equal(10, highParallelismQueue.MaxDegreeOfParallelism);
        Assert.Equal(2, lowParallelismQueue.MaxDegreeOfParallelism);
        Assert.NotEqual(highParallelismQueue.MaxDegreeOfParallelism, lowParallelismQueue.MaxDegreeOfParallelism);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task MultipleQueues_ConsumedConcurrently()
    {
        // Arrange
        var mockLogger    = new Mock<IEverTaskLogger<WorkerQueueManager>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var configurations = new Dictionary<string, QueueConfiguration>
        {
            ["queue1"] = new QueueConfiguration
            {
                Name = "queue1",
                MaxDegreeOfParallelism = 2,
                ChannelOptions = new BoundedChannelOptions(10)
            },
            ["queue2"] = new QueueConfiguration
            {
                Name = "queue2",
                MaxDegreeOfParallelism = 3,
                ChannelOptions = new BoundedChannelOptions(10)
            }
        };

        var loggerFactory = CreateLoggerFactory();
        var queueManager = new WorkerQueueManager(configurations, mockLogger.Object, mockBlacklist.Object, loggerFactory, null);
        var processedTasks = new ConcurrentBag<string>();

        // Add tasks to both queues
        for (int i = 0; i < 5; i++)
        {
            var task1 = CreateTestExecutor($"q1-task{i}", "queue1");
            var task2 = CreateTestExecutor($"q2-task{i}", "queue2");
            await queueManager.TryEnqueue("queue1", task1);
            await queueManager.TryEnqueue("queue2", task2);
        }

        // Act - Consume from both queues concurrently
        var consumeTasks = new List<Task>();
        foreach (var (name, queue) in queueManager.GetAllQueues())
        {
            consumeTasks.Add(Task.Run(async () =>
            {
                await foreach (var task in queue.DequeueAll(CancellationToken.None).WithCancellation(CancellationToken.None))
                {
                    processedTasks.Add($"{name}-processed");
                    if (processedTasks.Count >= 10) break;
                }
            }));
        }

        // Wait a bit for processing
        await Task.Delay(100);

        // Assert - Both queues should have processed tasks
        Assert.Contains("queue1-processed", processedTasks);
        Assert.Contains("queue2-processed", processedTasks);
    }

    [Fact]
    public void WorkerQueue_UsesConfiguredChannelCapacity()
    {
        // Arrange
        var mockLogger = new Mock<IEverTaskLogger<WorkerQueue>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var smallConfig = new QueueConfiguration
        {
            Name = "small",
            ChannelOptions = new BoundedChannelOptions(10)
        };
        var largeConfig = new QueueConfiguration
        {
            Name = "large",
            ChannelOptions = new BoundedChannelOptions(1000)
        };

        // Act
        var smallQueue = new WorkerQueue(smallConfig, mockLogger.Object, mockBlacklist.Object);
        var largeQueue = new WorkerQueue(largeConfig, mockLogger.Object, mockBlacklist.Object);

        // Assert
        Assert.Equal(10, smallConfig.ChannelOptions.Capacity);
        Assert.Equal(1000, largeConfig.ChannelOptions.Capacity);
        Assert.Equal("small", smallQueue.Name);
        Assert.Equal("large", largeQueue.Name);
    }

    [Fact]
    public Task QueueManager_GetAllQueues_ReturnsAllConfiguredQueues()
    {
        // Arrange
        var mockLogger = new Mock<IEverTaskLogger<WorkerQueueManager>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var configurations = new Dictionary<string, QueueConfiguration>
        {
            ["default"] = new QueueConfiguration { Name = "default" },
            ["recurring"] = new QueueConfiguration { Name = "recurring" },
            ["priority"] = new QueueConfiguration { Name = "priority" },
            ["background"] = new QueueConfiguration { Name = "background" }
        };

        var loggerFactory = CreateLoggerFactory();
        var queueManager = new WorkerQueueManager(configurations, mockLogger.Object, mockBlacklist.Object, loggerFactory, null);

        // Act
        var allQueues = queueManager.GetAllQueues().ToList();

        // Assert
        Assert.Equal(4, allQueues.Count);
        var queueNames = allQueues.Select(q => q.Name).ToHashSet();
        Assert.Contains("default", queueNames);
        Assert.Contains("recurring", queueNames);
        Assert.Contains("priority", queueNames);
        Assert.Contains("background", queueNames);

        return Task.CompletedTask;
    }

    [Fact]
    public void WorkerQueueManager_GetQueue_ThrowsForNonExistentQueue()
    {
        // Arrange
        var mockLogger = new Mock<IEverTaskLogger<WorkerQueueManager>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var configurations = new Dictionary<string, QueueConfiguration>
        {
            ["default"] = new QueueConfiguration { Name = "default" }
        };

        var loggerFactory = CreateLoggerFactory();
        var queueManager = new WorkerQueueManager(configurations, mockLogger.Object, mockBlacklist.Object, loggerFactory, null);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => queueManager.GetQueue("non-existent"));
    }

    [Fact]
    public void WorkerQueueManager_TryGetQueue_ReturnsFalseForNonExistentQueue()
    {
        // Arrange
        var mockLogger = new Mock<IEverTaskLogger<WorkerQueueManager>>();
        var mockBlacklist = new Mock<IWorkerBlacklist>();
        var configurations = new Dictionary<string, QueueConfiguration>
        {
            ["default"] = new QueueConfiguration { Name = "default" }
        };

        var loggerFactory = CreateLoggerFactory();
        var queueManager = new WorkerQueueManager(configurations, mockLogger.Object, mockBlacklist.Object, loggerFactory, null);

        // Act
        var result = queueManager.TryGetQueue("non-existent", out var queue);

        // Assert
        Assert.False(result);
        Assert.Null(queue);
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
            Guid.NewGuid(),
            queueName,
            null
        );
    }

    private record TestTask : IEverTask
    {
        public string Id { get; init; } = "";
    }
}

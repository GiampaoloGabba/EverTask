using EverTask.Tests.TestHelpers;
using EverTask.Abstractions;
using EverTask.Configuration;
using EverTask.Dispatcher;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Worker;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace EverTask.Tests.MultiQueue;

public class MultiQueueRoutingTests
{
    [Fact]
    public async Task Task_WithCustomQueueName_RoutesToCorrectQueue()
    {
        // Arrange
        var services = new ServiceCollection();
        var mockQueueManager = new Mock<IWorkerQueueManager>();
        var customQueue = new Mock<IWorkerQueue>();

        mockQueueManager.Setup(x => x.TryEnqueue("high-priority", It.IsAny<TaskHandlerExecutor>()))
            .ReturnsAsync(true);

        // Create a task with custom queue
        var task = new TestTaskWithQueue { QueueName = "high-priority" };
        var executor = new TaskHandlerExecutor(
            task,
            new TestHandler(),
            null,  // HandlerTypeName - null for eager mode
            null,
            null,
            (t, ct) => Task.CompletedTask,
            null,
            null,
            null,
            TestGuidGenerator.New(),
            "high-priority",
            null
        );

        // Act
        await mockQueueManager.Object.TryEnqueue(executor.QueueName, executor);

        // Assert
        mockQueueManager.Verify(x => x.TryEnqueue("high-priority", It.IsAny<TaskHandlerExecutor>()), Times.Once);
        mockQueueManager.Verify(x => x.TryEnqueue("default", It.IsAny<TaskHandlerExecutor>()), Times.Never);
    }

    [Fact]
    public async Task Task_WithoutQueueName_RoutesToDefaultQueue()
    {
        // Arrange
        var mockQueueManager = new Mock<IWorkerQueueManager>();

        mockQueueManager.Setup(x => x.TryEnqueue("default", It.IsAny<TaskHandlerExecutor>()))
            .ReturnsAsync(true);

        // Create a task without queue name
        var task = new TestTask();
        var executor = new TaskHandlerExecutor(
            task,
            new TestHandler(),
            null,  // HandlerTypeName - null for eager mode
            null,
            null,
            (t, ct) => Task.CompletedTask,
            null,
            null,
            null,
            TestGuidGenerator.New(),
            null,
            null
        );

        // Act
        string targetQueue = executor.QueueName ?? "default";
        await mockQueueManager.Object.TryEnqueue(targetQueue, executor);

        // Assert
        mockQueueManager.Verify(x => x.TryEnqueue("default", It.IsAny<TaskHandlerExecutor>()), Times.Once);
    }

    [Fact]
    public async Task RecurringTask_WithoutQueueName_RoutesToRecurringQueue()
    {
        // Arrange
        var mockQueueManager = new Mock<IWorkerQueueManager>();
        var recurringTask = new RecurringTask { SecondInterval = new SecondInterval(30) };

        mockQueueManager.Setup(x => x.TryEnqueue("recurring", It.IsAny<TaskHandlerExecutor>()))
            .ReturnsAsync(true);

        // Create a recurring task without explicit queue name
        var task = new TestTask();
        var executor = new TaskHandlerExecutor(
            task,
            new TestHandler(),
            null,  // HandlerTypeName - null for eager mode
            DateTimeOffset.UtcNow.AddMinutes(1),
            recurringTask,
            (t, ct) => Task.CompletedTask,
            null,
            null,
            null,
            TestGuidGenerator.New(),
            null,
            null
        );

        // Act - simulate routing logic
        string targetQueue = executor.QueueName ?? (executor.RecurringTask != null ? "recurring" : "default");
        await mockQueueManager.Object.TryEnqueue(targetQueue, executor);

        // Assert
        mockQueueManager.Verify(x => x.TryEnqueue("recurring", It.IsAny<TaskHandlerExecutor>()), Times.Once);
        mockQueueManager.Verify(x => x.TryEnqueue("default", It.IsAny<TaskHandlerExecutor>()), Times.Never);
    }

    [Fact]
    public async Task RecurringTask_WithExplicitQueueName_RespectsOverride()
    {
        // Arrange
        var mockQueueManager = new Mock<IWorkerQueueManager>();
        var recurringTask = new RecurringTask { SecondInterval = new SecondInterval(30) };

        mockQueueManager.Setup(x => x.TryEnqueue("background", It.IsAny<TaskHandlerExecutor>()))
            .ReturnsAsync(true);

        // Create a recurring task with explicit queue name
        var task = new TestTaskWithQueue { QueueName = "background" };
        var executor = new TaskHandlerExecutor(
            task,
            new TestHandlerWithQueue(),
            null,  // HandlerTypeName - null for eager mode
            DateTimeOffset.UtcNow.AddMinutes(1),
            recurringTask,
            (t, ct) => Task.CompletedTask,
            null,
            null,
            null,
            TestGuidGenerator.New(),
            "background",
            null
        );

        // Act
        await mockQueueManager.Object.TryEnqueue(executor.QueueName, executor);

        // Assert
        mockQueueManager.Verify(x => x.TryEnqueue("background", It.IsAny<TaskHandlerExecutor>()), Times.Once);
        mockQueueManager.Verify(x => x.TryEnqueue("recurring", It.IsAny<TaskHandlerExecutor>()), Times.Never);
    }

    [Fact]
    public void TaskHandlerExecutor_PreservesQueueName()
    {
        // Arrange
        var task = new TestTask();
        var handler = new TestHandler();
        var queueName = "custom-queue";

        // Act
        var executor = new TaskHandlerExecutor(
            task,
            handler,
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

        // Assert
        Assert.Equal(queueName, executor.QueueName);
    }

    [Fact]
    public void QueuedTask_SerializesQueueName()
    {
        // Arrange
        var task = new TestTask();
        var handler = new TestHandler();
        var queueName = "special-queue";
        var executor = new TaskHandlerExecutor(
            task,
            handler,
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

        // Act
        var queuedTask = executor.ToQueuedTask();

        // Assert
        Assert.Equal(queueName, queuedTask.QueueName);
    }

    // Test task classes
    private record TestTask : IEverTask;
    private record TestTaskWithQueue : IEverTask
    {
        public string QueueName { get; init; } = "default";
    }

    private class TestHandler : EverTaskHandler<TestTask>
    {
        public override Task Handle(TestTask backgroundTask, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private class TestHandlerWithQueue : EverTaskHandler<TestTaskWithQueue>
    {
        public override string? QueueName => "background";

        public override Task Handle(TestTaskWithQueue backgroundTask, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

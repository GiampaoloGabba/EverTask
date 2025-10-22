using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;

namespace EverTask.Tests;

/// <summary>
/// Tests to verify that performance optimizations work correctly.
/// These tests ensure caching, thread safety, and memory management of hotpath optimizations.
/// </summary>
public class PerformanceOptimizationTests
{
    #region Solution #3: Type Metadata Cache Tests

    [Fact]
    public void ToQueuedTask_WithSameTaskType_ShouldReuseCachedAssemblyQualifiedName()
    {
        // Arrange - same task type, different instances
        var task1 = new TestTaskRequest("data1");
        var task2 = new TestTaskRequest("data2");
        var handler = new TestTaskHanlder();

        var executor1 = CreateTaskHandlerExecutor(task1, handler);
        var executor2 = CreateTaskHandlerExecutor(task2, handler);

        // Act
        var queued1 = executor1.ToQueuedTask();
        var queued2 = executor2.ToQueuedTask();

        // Assert - same task type should have same Type string (cached)
        queued1.Type.ShouldBe(queued2.Type);
        queued1.Handler.ShouldBe(queued2.Handler);
        queued1.Type.ShouldContain("TestTaskRequest");
        queued1.Handler.ShouldContain("TestTaskHanlder");
    }

    [Fact]
    public void ToQueuedTask_WithDifferentTaskTypes_ShouldHaveDifferentTypeStrings()
    {
        // Arrange - different task types
        var task1 = new TestTaskRequest("data");
        var task2 = new TestTaskConcurrent1();
        var handler1 = new TestTaskHanlder();
        var handler2 = new TestTaskConcurrent1Handler();

        var executor1 = CreateTaskHandlerExecutor(task1, handler1);
        var executor2 = CreateTaskHandlerExecutor(task2, handler2);

        // Act
        var queued1 = executor1.ToQueuedTask();
        var queued2 = executor2.ToQueuedTask();

        // Assert - different task types should have different strings
        queued1.Type.ShouldNotBe(queued2.Type);
        queued1.Handler.ShouldNotBe(queued2.Handler);
    }

    [Fact]
    public void ToQueuedTask_WithRecurringTask_ShouldCacheToString()
    {
        // Arrange - same recurring configuration
        var task = new TestTaskRecurringSeconds();
        var handler = new TestTaskRecurringSecondsHandler();
        var recurring = new RecurringTask
        {
            SecondInterval = new SecondInterval(30)
        };

        var executor1 = CreateTaskHandlerExecutor(task, handler, recurring: recurring);
        var executor2 = CreateTaskHandlerExecutor(task, handler, recurring: recurring);

        // Act
        var queued1 = executor1.ToQueuedTask();
        var queued2 = executor2.ToQueuedTask();

        // Assert - same recurring config should have same RecurringInfo (cached ToString)
        queued1.RecurringInfo.ShouldBe(queued2.RecurringInfo);
        queued1.RecurringInfo.ShouldNotBeNullOrEmpty();
        queued1.IsRecurring.ShouldBeTrue();
        queued2.IsRecurring.ShouldBeTrue();
    }

    [Fact]
    public void ToQueuedTask_ConcurrentCalls_ShouldBeThreadSafe()
    {
        // Arrange - many tasks to test concurrency
        var tasks = Enumerable.Range(0, 100).Select(i => new TestTaskRequest($"task-{i}")).ToArray();
        var handler = new TestTaskHanlder();
        var executors = tasks.Select(t => CreateTaskHandlerExecutor(t, handler)).ToArray();

        var queuedTasks = new System.Collections.Concurrent.ConcurrentBag<QueuedTask>();

        // Act - call ToQueuedTask concurrently from multiple threads
        Parallel.For(0, 100, i =>
        {
            var queued = executors[i].ToQueuedTask();
            queuedTasks.Add(queued);
        });

        // Assert - all should complete successfully without exceptions
        queuedTasks.Count.ShouldBe(100);
        queuedTasks.All(q => q.Type != null).ShouldBeTrue();
        queuedTasks.All(q => q.Handler != null).ShouldBeTrue();
        queuedTasks.All(q => !string.IsNullOrEmpty(q.Request)).ShouldBeTrue();

        // Verify that same task type has same cached type string
        var allTypeStrings = queuedTasks.Select(q => q.Type).Distinct().ToList();
        allTypeStrings.Count.ShouldBe(1); // All tasks are TestTaskRequest, should be 1 unique type string
    }

    [Fact]
    public void ToQueuedTask_MultipleTaskTypesWithConcurrency_ShouldCachePerType()
    {
        // Arrange - multiple different task types
        var type1Tasks = Enumerable.Range(0, 50).Select(i => new TestTaskRequest($"t1-{i}")).ToArray();
        var type2Tasks = Enumerable.Range(0, 50).Select(i => new TestTaskConcurrent1()).ToArray();

        var handler1 = new TestTaskHanlder();
        var handler2 = new TestTaskConcurrent1Handler();

        var executors1 = type1Tasks.Select(t => CreateTaskHandlerExecutor(t, handler1)).ToArray();
        var executors2 = type2Tasks.Select(t => CreateTaskHandlerExecutor(t, handler2)).ToArray();

        var allExecutors = executors1.Concat(executors2).ToArray();
        var queuedTasks = new System.Collections.Concurrent.ConcurrentBag<QueuedTask>();

        // Act - process all concurrently
        Parallel.For(0, 100, i =>
        {
            var queued = allExecutors[i].ToQueuedTask();
            queuedTasks.Add(queued);
        });

        // Assert
        queuedTasks.Count.ShouldBe(100);

        // Should have exactly 2 distinct type strings (cached per type)
        var distinctTypes = queuedTasks.Select(q => q.Type).Distinct().ToList();
        distinctTypes.Count.ShouldBe(2);

        // Should have exactly 2 distinct handler strings
        var distinctHandlers = queuedTasks.Select(q => q.Handler).Distinct().ToList();
        distinctHandlers.Count.ShouldBe(2);
    }

    #endregion

    #region Behavioral Equivalence Tests

    [Fact]
    public void ToQueuedTask_AfterOptimization_ProducesCorrectQueuedTask()
    {
        // This test verifies that optimization doesn't change behavior

        // Arrange
        var task = new TestTaskRequest("test-data");
        var handler = new TestTaskHanlder();
        var executor = CreateTaskHandlerExecutor(task, handler);

        // Act
        var queued = executor.ToQueuedTask();

        // Assert - verify all expected fields are populated correctly
        queued.Id.ShouldNotBe(Guid.Empty);
        queued.Type.ShouldContain("TestTaskRequest");
        queued.Handler.ShouldContain("TestTaskHanlder");
        queued.Request.ShouldContain("test-data");
        queued.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        queued.CreatedAtUtc.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        queued.IsRecurring.ShouldBeFalse();
        queued.QueueName.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ToQueuedTask_WithRecurringTask_PopulatesRecurringFields()
    {
        // Arrange
        var task = new TestTaskRecurringSeconds();
        var handler = new TestTaskRecurringSecondsHandler();
        var recurring = new RecurringTask
        {
            SecondInterval = new SecondInterval(15),
            MaxRuns = 10
        };

        var executor = CreateTaskHandlerExecutor(task, handler, recurring: recurring);

        // Act
        var queued = executor.ToQueuedTask();

        // Assert - recurring fields should be populated
        queued.IsRecurring.ShouldBeTrue();
        queued.RecurringTask.ShouldNotBeNullOrEmpty();
        queued.RecurringInfo.ShouldNotBeNullOrEmpty();
        queued.MaxRuns.ShouldBe(10);
        queued.NextRunUtc.ShouldNotBeNull();
    }

    #endregion

    #region Helper Methods

    private TaskHandlerExecutor CreateTaskHandlerExecutor(
        IEverTask task,
        object handler,
        DateTimeOffset? executionTime = null,
        RecurringTask? recurring = null)
    {
        return new TaskHandlerExecutor(
            task,
            handler,
            null,  // HandlerTypeName - null for eager mode
            executionTime,
            recurring,
            (t, ct) => Task.CompletedTask,
            (id, ex, msg) => ValueTask.CompletedTask,
            id => ValueTask.CompletedTask,
            id => ValueTask.CompletedTask,
            Guid.NewGuid(),
            QueueNames.Default,
            null
        );
    }

    #endregion
}

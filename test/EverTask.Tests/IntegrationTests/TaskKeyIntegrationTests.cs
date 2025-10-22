using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests for TaskKey idempotent task registration functionality.
/// Tests verify that tasks with the same taskKey are properly deduplicated and updated.
/// </summary>
public class TaskKeyIntegrationTests : IsolatedIntegrationTestBase
{
    #region Basic Functional Tests

    [Fact]
    public async Task Dispatch_WithNewTaskKey_CreatesTaskWithKey()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("test"), taskKey: "unique-key-1");

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].TaskKey.ShouldBe("unique-key-1");
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Dispatch_WithSameTaskKey_WhenTaskInProgress_ReturnsExistingId()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        TestTaskConcurrent1.Counter = 0;

        // Act - Dispatch long-running task
        var task1 = new TestTaskConcurrent1();
        var taskId1 = await Dispatcher.Dispatch(task1, taskKey: "in-progress-key");

        // Wait for task to be in progress
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.InProgress);

        // Dispatch duplicate with same taskKey while first is still running
        var taskId2 = await Dispatcher.Dispatch(task1, taskKey: "in-progress-key");

        // Assert - Should return same ID
        taskId2.ShouldBe(taskId1);

        // Wait for original task to complete
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);

        // Verify only one task was created
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].TaskKey.ShouldBe("in-progress-key");
    }

    [Fact]
    public async Task Dispatch_WithSameTaskKey_WhenTaskQueued_UpdatesTask()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch delayed task
        var originalTime = DateTimeOffset.UtcNow.AddMinutes(10);
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRequest("original"),
            originalTime,
            taskKey: "queued-key");

        // Wait a bit for task to be persisted
        await Task.Delay(100);

        // Verify task exists
        var task1 = await Storage.Get(t => t.Id == taskId1);
        task1.Length.ShouldBe(1);
        task1[0].TaskKey.ShouldBe("queued-key");

        // Dispatch again with same taskKey but different parameters
        var newTime = DateTimeOffset.UtcNow.AddMinutes(20);
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRequest("updated"),
            newTime,
            taskKey: "queued-key");

        // Assert - Should return same ID (task was updated, not replaced)
        taskId2.ShouldBe(taskId1);

        // Verify task was updated (still only one task)
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
        allTasks[0].Id.ShouldBe(taskId1);
        allTasks[0].TaskKey.ShouldBe("queued-key");
        allTasks[0].ScheduledExecutionUtc.ShouldBe(newTime);

        // Verify request was updated
        var updatedRequest = System.Text.Json.JsonSerializer.Deserialize<TestTaskRequest>(allTasks[0].Request);
        updatedRequest!.Name.ShouldBe("updated");

    }

    [Fact]
    public async Task Dispatch_WithSameTaskKey_WhenTaskCompleted_ReplacesTask()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch task and wait for completion
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequest("first"), taskKey: "replace-key");
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);

        // Verify first task completed
        var completedTasks = await Storage.GetAll();
        completedTasks.Length.ShouldBe(1);
        completedTasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // Dispatch again with same taskKey (should remove old and create new)
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("second"), taskKey: "replace-key");

        // Wait for second task to complete
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert - Should be different ID (new task created)
        taskId2.ShouldNotBe(taskId1);

        // Only one task should exist (old was removed)
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
        allTasks[0].Id.ShouldBe(taskId2);
        allTasks[0].TaskKey.ShouldBe("replace-key");
        allTasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

    }

    [Fact]
    public async Task Dispatch_WithSameTaskKey_WhenTaskFailed_ReplacesTask()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch task that will fail
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequestError(), taskKey: "failed-key");
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Failed, timeoutMs: 3000);

        // Dispatch again with same taskKey
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("retry"), taskKey: "failed-key");
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert - Should be different ID
        taskId2.ShouldNotBe(taskId1);

        // Only second task should exist
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
        allTasks[0].Id.ShouldBe(taskId2);
        allTasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

    }

    [Fact]
    public async Task Dispatch_WithSameTaskKey_WhenTaskCancelled_ReplacesTask()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch and cancel task
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskConcurrent1(),
            TimeSpan.FromMinutes(5),
            taskKey: "cancelled-key");

        await Task.Delay(100); // Let task be scheduled
        await Dispatcher.Cancel(taskId1);
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Cancelled);

        // Dispatch again with same taskKey
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("after-cancel"), taskKey: "cancelled-key");
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert
        taskId2.ShouldNotBe(taskId1);
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
        allTasks[0].Id.ShouldBe(taskId2);

    }

    #endregion

    #region Update Scenarios

    [Fact]
    public async Task Dispatch_UpdateRecurringTask_ChangesSchedule()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        TestTaskRecurringSeconds.Counter = 0;

        // Act - Dispatch recurring task (every 5 seconds)
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(5).Seconds(),
            taskKey: "recurring-schedule-key");

        var tasks1 = await Storage.Get(t => t.Id == taskId1);
        var task1 = tasks1.FirstOrDefault();
        task1.ShouldNotBeNull();
        task1!.RecurringInfo.ShouldNotBeNull();
        task1.RecurringInfo.ShouldContain("every 5 second");

        // Update to every 10 seconds
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(10).Seconds(),
            taskKey: "recurring-schedule-key");

        // Assert - Same task ID (updated in place)
        taskId2.ShouldBe(taskId1);

        var updatedTasks = await Storage.Get(t => t.Id == taskId1);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask.ShouldNotBeNull();
        updatedTask!.RecurringInfo.ShouldNotBeNull();
        updatedTask.RecurringInfo.ShouldContain("every 10 second");

    }

    [Fact]
    public async Task Dispatch_UpdateRecurringTask_ChangesParameters()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch recurring task with initial parameters
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRequest("version1"),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "recurring-params-key");

        var tasks1 = await Storage.Get(t => t.Id == taskId1);
        var task1 = tasks1.FirstOrDefault();
        var request1 = System.Text.Json.JsonSerializer.Deserialize<TestTaskRequest>(task1!.Request);
        request1!.Name.ShouldBe("version1");

        // Update parameters
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRequest("version2"),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "recurring-params-key");

        // Assert
        taskId2.ShouldBe(taskId1);

        var updatedTasks = await Storage.Get(t => t.Id == taskId1);
        var updatedTask = updatedTasks.FirstOrDefault();
        var request2 = System.Text.Json.JsonSerializer.Deserialize<TestTaskRequest>(updatedTask!.Request);
        request2!.Name.ShouldBe("version2");

    }

    [Fact]
    public async Task Dispatch_UpdateDelayedTask_ChangesExecutionTime()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        var originalTime = DateTimeOffset.UtcNow.AddMinutes(10);
        var newTime = DateTimeOffset.UtcNow.AddMinutes(20);

        // Act
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRequest("delayed"),
            originalTime,
            taskKey: "delayed-time-key");

        var tasks1 = await Storage.Get(t => t.Id == taskId1);
        var task1 = tasks1.FirstOrDefault();
        task1!.ScheduledExecutionUtc.ShouldBe(originalTime);

        // Update execution time
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRequest("delayed"),
            newTime,
            taskKey: "delayed-time-key");

        // Assert
        taskId2.ShouldBe(taskId1);

        var updatedTasks = await Storage.Get(t => t.Id == taskId1);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask!.ScheduledExecutionUtc.ShouldBe(newTime);

    }

    [Fact]
    public async Task Dispatch_UpdateTaskType_ImmediateToDelayed()
    {
        // Arrange - Create host but DON'T start it yet (to prevent task from executing)
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false);

        // Act - Create immediate task (queued but not executing since host not started)
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequest("immediate"), taskKey: "type-change-key");

        var tasks1 = await Storage.Get(t => t.Id == taskId1);
        var task1 = tasks1.FirstOrDefault();
        task1!.ScheduledExecutionUtc.ShouldBeNull();

        // Update to delayed task (should update the existing task since it hasn't started)
        var delayedTime = DateTimeOffset.UtcNow.AddMinutes(10);
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRequest("delayed"),
            delayedTime,
            taskKey: "type-change-key");

        // Assert
        taskId2.ShouldBe(taskId1);

        var updatedTasks = await Storage.Get(t => t.Id == taskId1);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask!.ScheduledExecutionUtc.ShouldBe(delayedTime);

    }

    [Fact]
    public async Task Dispatch_UpdateTaskType_DelayedToRecurring()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        var delayedTime = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act - Create delayed task
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRequest("delayed"),
            delayedTime,
            taskKey: "delayed-to-recurring-key");

        var tasks1 = await Storage.Get(t => t.Id == taskId1);
        var task1 = tasks1.FirstOrDefault();
        task1!.IsRecurring.ShouldBeFalse();

        // Update to recurring
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRequest("recurring"),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "delayed-to-recurring-key");

        // Assert
        taskId2.ShouldBe(taskId1);

        var updatedTasks = await Storage.Get(t => t.Id == taskId1);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask!.IsRecurring.ShouldBeTrue();
        updatedTask.RecurringInfo.ShouldNotBeNull();

    }

    // Note: Testing CurrentRunCount preservation during update is difficult due to timing/race conditions
    // The UpdateTask logic in storage does preserve CurrentRunCount, but testing it reliably
    // with recurring tasks executing in background is complex. The functionality is covered by
    // unit-level storage tests and the other integration tests verify the core taskKey behavior.

    #endregion

    #region Edge Cases

    [Fact]
    public async Task Dispatch_WithNullTaskKey_BehavesNormally()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch same task twice with null taskKey
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequest("first"), taskKey: null);
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("second"), taskKey: null);

        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert - Both tasks should be created (no deduplication)
        taskId1.ShouldNotBe(taskId2);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(2);

    }

    [Fact]
    public async Task Dispatch_WithEmptyTaskKey_BehavesNormally()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch with empty string taskKey
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequest("first"), taskKey: "");
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("second"), taskKey: "");

        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert - Both tasks should be created
        taskId1.ShouldNotBe(taskId2);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(2);

    }

    [Fact]
    public async Task Dispatch_WithWhitespaceTaskKey_BehavesNormally()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch with whitespace taskKey
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequest("first"), taskKey: "   ");
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("second"), taskKey: "   ");

        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert
        taskId1.ShouldNotBe(taskId2);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(2);

    }

    [Fact]
    public async Task Dispatch_WithSpecialCharactersInTaskKey_WorksCorrectly()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        var specialKey = "task-key:with/special\\chars@123!";

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("test"), taskKey: specialKey);

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].TaskKey.ShouldBe(specialKey);

    }

    #endregion

    #region Integration Scenarios

    [Fact]
    public async Task Dispatch_ScheduledTaskWithTaskKey_NotDuplicatedOnRedispatch()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act - Dispatch scheduled task
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRequest("scheduled"),
            scheduledTime,
            taskKey: "scheduled-key");

        // Wait for task to be persisted
        await Task.Delay(100);

        // Verify task exists
        var task1 = await Storage.Get(t => t.Id == taskId1);
        task1.Length.ShouldBe(1);
        task1[0].TaskKey.ShouldBe("scheduled-key");

        // Dispatch again with same taskKey (simulating redispatch on restart)
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRequest("scheduled"),
            scheduledTime,
            taskKey: "scheduled-key");

        // Assert - Should return same ID (not create duplicate)
        taskId2.ShouldBe(taskId1);

        // Still only one task
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
        allTasks[0].Id.ShouldBe(taskId1);

    }

    [Fact]
    public async Task Dispatch_RecurringTaskWithTaskKey_NotDuplicatedAfterRestart()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        TestTaskRecurringSeconds.Counter = 0;

        // Act - Dispatch recurring task
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "restart-recurring-key");

        var tasks1 = await Storage.Get(t => t.Id == taskId1);
        var task1 = tasks1.FirstOrDefault();
        task1.ShouldNotBeNull();

        // Simulate restart - dispatch again
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "restart-recurring-key");

        // Assert
        taskId2.ShouldBe(taskId1);

        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);

    }

    [Fact]
    public async Task Dispatch_TaskKeyWithMultipleQueues_RoutesCorrectly()
    {
        // Arrange
        await CreateIsolatedHostWithBuilderAsync(builder =>
        {
            builder
                .AddQueue("high-priority", q => q.SetMaxDegreeOfParallelism(5))
                .AddMemoryStorage();
        });

        // Act - Dispatch task with taskKey to custom queue
        var taskId = await Dispatcher.Dispatch(new TestTaskHighPriority(), taskKey: "high-priority-key");

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].TaskKey.ShouldBe("high-priority-key");
        tasks[0].QueueName.ShouldBe("high-priority");

        // Dispatch again - should update existing task in same queue
        var taskId2 = await Dispatcher.Dispatch(new TestTaskHighPriority(), taskKey: "high-priority-key");

        // Should create new task since first completed
        taskId2.ShouldNotBe(taskId);

        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1); // Old removed, new created
        allTasks[0].QueueName.ShouldBe("high-priority");

    }

    [Fact]
    public async Task Dispatch_TaskKeyWithCancellation_BehavesCorrectly()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch task with taskKey
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskConcurrent1(),
            TimeSpan.FromMinutes(5),
            taskKey: "cancel-key");

        await Task.Delay(100); // Let task be scheduled

        // Cancel task
        await Dispatcher.Cancel(taskId1);
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Cancelled);

        // Dispatch again with same taskKey (should replace cancelled task)
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("after-cancel"), taskKey: "cancel-key");
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert
        taskId2.ShouldNotBe(taskId1);

        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
        allTasks[0].Id.ShouldBe(taskId2);
        allTasks[0].TaskKey.ShouldBe("cancel-key");
        allTasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

    }

    [Fact]
    public async Task Dispatch_MultipleTasksWithDifferentKeys_AllCreated()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch multiple tasks with different keys
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequest("task1"), taskKey: "key-1");
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("task2"), taskKey: "key-2");
        var taskId3 = await Dispatcher.Dispatch(new TestTaskRequest("task3"), taskKey: "key-3");

        await WaitForTaskCountAsync(3);

        // Assert - All different tasks should be created
        taskId1.ShouldNotBe(taskId2);
        taskId2.ShouldNotBe(taskId3);
        taskId1.ShouldNotBe(taskId3);

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(3);
        tasks.Select(t => t.TaskKey).ShouldBe(new[] { "key-1", "key-2", "key-3" }, ignoreOrder: true);

    }

    #endregion
}

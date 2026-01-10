using EverTask.Scheduler.Recurring;
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

        // Assert - Both tasks should be created (no deduplication, filter by ID to avoid interference)
        taskId1.ShouldNotBe(taskId2);

        var task1 = await Storage.Get(t => t.Id == taskId1);
        var task2 = await Storage.Get(t => t.Id == taskId2);
        task1.Length.ShouldBe(1);
        task2.Length.ShouldBe(1);

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

        // Assert - Both tasks should be created (filter by ID to avoid interference)
        taskId1.ShouldNotBe(taskId2);

        var task1 = await Storage.Get(t => t.Id == taskId1);
        var task2 = await Storage.Get(t => t.Id == taskId2);
        task1.Length.ShouldBe(1);
        task2.Length.ShouldBe(1);

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

        // Assert (filter by ID to avoid interference from other tests)
        taskId1.ShouldNotBe(taskId2);

        var task1 = await Storage.Get(t => t.Id == taskId1);
        var task2 = await Storage.Get(t => t.Id == taskId2);
        task1.Length.ShouldBe(1);
        task2.Length.ShouldBe(1);

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

        // Assert - filter by ID to avoid interference from other tests
        var tasks = await Storage.Get(t => t.Id == taskId);
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
        const string taskKey = "high-priority-key";

        // Act - Dispatch task with taskKey to custom queue
        var taskId = await Dispatcher.Dispatch(new TestTaskHighPriority(), taskKey: taskKey);

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert - filter by TaskKey to avoid interference from other tests
        var tasks = await Storage.Get(t => t.TaskKey == taskKey);
        tasks.Length.ShouldBe(1);
        tasks[0].TaskKey.ShouldBe(taskKey);
        tasks[0].QueueName.ShouldBe("high-priority");

        // Dispatch again - should update existing task in same queue
        var taskId2 = await Dispatcher.Dispatch(new TestTaskHighPriority(), taskKey: taskKey);

        // Should create new task since first completed
        taskId2.ShouldNotBe(taskId);

        var tasksAfterSecond = await Storage.Get(t => t.TaskKey == taskKey);
        tasksAfterSecond.Length.ShouldBe(1); // Old removed, new created
        tasksAfterSecond[0].QueueName.ShouldBe("high-priority");

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

    #region Recurring Task with TaskKey - History Preservation

    [Fact]
    public async Task Dispatch_RecurringTaskWithTaskKey_WhenCompleted_PreservesHistoryAndUpdates()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch recurring task and wait for first execution
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds(),
            taskKey: "recurring-completed-key");

        // Wait for first execution to complete
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Get task state after first execution
        var tasksAfterFirstRun = await Storage.Get(t => t.Id == taskId1);
        var taskAfterFirstRun = tasksAfterFirstRun.FirstOrDefault();
        taskAfterFirstRun.ShouldNotBeNull();
        taskAfterFirstRun!.CurrentRunCount.ShouldNotBeNull();
        taskAfterFirstRun.CurrentRunCount!.Value.ShouldBeGreaterThanOrEqualTo(1);
        taskAfterFirstRun.LastExecutionUtc.ShouldNotBeNull();
        var originalCreatedAt = taskAfterFirstRun.CreatedAtUtc;
        var originalCurrentRunCount = taskAfterFirstRun.CurrentRunCount;
        var originalLastExecutionUtc = taskAfterFirstRun.LastExecutionUtc;

        // Dispatch again with same taskKey (simulating app restart)
        // For RECURRING tasks, this should UPDATE (not remove/recreate)
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds(), // Changed interval
            taskKey: "recurring-completed-key");

        // Assert - Should return SAME ID (updated, not replaced)
        taskId2.ShouldBe(taskId1);

        // Verify history was preserved
        var tasksAfterRedispatch = await Storage.Get(t => t.Id == taskId1);
        var taskAfterRedispatch = tasksAfterRedispatch.FirstOrDefault();
        taskAfterRedispatch.ShouldNotBeNull();

        // CreatedAtUtc should be preserved (not reset)
        taskAfterRedispatch!.CreatedAtUtc.ShouldBe(originalCreatedAt);

        // CurrentRunCount should be preserved
        taskAfterRedispatch.CurrentRunCount.ShouldBe(originalCurrentRunCount);

        // LastExecutionUtc should be preserved
        taskAfterRedispatch.LastExecutionUtc.ShouldBe(originalLastExecutionUtc);

        // Still only one task
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatch_RecurringTaskWithTaskKey_WhenFailed_PreservesHistoryAndUpdates()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Create a recurring task (hourly, so it won't execute during test)
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "recurring-failed-key");

        // Wait for task to be persisted
        await Task.Delay(100);

        // Get task state and verify it exists
        var tasksAfterCreate = await Storage.Get(t => t.Id == taskId1);
        var taskAfterCreate = tasksAfterCreate.FirstOrDefault();
        taskAfterCreate.ShouldNotBeNull();
        taskAfterCreate!.IsRecurring.ShouldBeTrue();
        var originalCreatedAt = taskAfterCreate.CreatedAtUtc;

        // Manually set task status to Failed to simulate a failed execution
        await Storage.SetStatus(taskId1, QueuedTaskStatus.Failed, new Exception("Simulated failure"), AuditLevel.Full);

        // Verify task is now Failed
        var tasksAfterFailure = await Storage.Get(t => t.Id == taskId1);
        tasksAfterFailure.FirstOrDefault()!.Status.ShouldBe(QueuedTaskStatus.Failed);

        // Dispatch again with same taskKey
        // For RECURRING tasks, this should UPDATE (not remove/recreate)
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Hours(), // Different interval
            taskKey: "recurring-failed-key");

        // Assert - Should return SAME ID (updated, not replaced)
        taskId2.ShouldBe(taskId1);

        // CreatedAtUtc should be preserved
        var tasksAfterRedispatch = await Storage.Get(t => t.Id == taskId1);
        var taskAfterRedispatch = tasksAfterRedispatch.FirstOrDefault();
        taskAfterRedispatch.ShouldNotBeNull();
        taskAfterRedispatch!.CreatedAtUtc.ShouldBe(originalCreatedAt);

        // Still only one task
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatch_RecurringTaskWithTaskKey_PreservesNextRunUtcIfInFuture()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch recurring task with future execution
        var taskId1 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "preserve-nextrun-key");

        // Get the NextRunUtc that was calculated
        var tasksAfterFirstDispatch = await Storage.Get(t => t.Id == taskId1);
        var taskAfterFirstDispatch = tasksAfterFirstDispatch.FirstOrDefault();
        taskAfterFirstDispatch.ShouldNotBeNull();
        var originalNextRunUtc = taskAfterFirstDispatch!.NextRunUtc;
        originalNextRunUtc.ShouldNotBeNull();

        // Small delay to ensure we're testing that NextRunUtc is preserved, not recalculated
        await Task.Delay(100);

        // Dispatch again with same taskKey (simulating app restart)
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "preserve-nextrun-key");

        // Assert - Same task ID
        taskId2.ShouldBe(taskId1);

        // NextRunUtc should be preserved (not recalculated to a new time)
        var tasksAfterRedispatch = await Storage.Get(t => t.Id == taskId1);
        var taskAfterRedispatch = tasksAfterRedispatch.FirstOrDefault();
        taskAfterRedispatch.ShouldNotBeNull();
        taskAfterRedispatch!.NextRunUtc.ShouldBe(originalNextRunUtc);
    }

    [Fact]
    public async Task Dispatch_NonRecurringTaskWithTaskKey_WhenCompleted_StillReplacesTask()
    {
        // Arrange
        await CreateIsolatedHostAsync();
        const string taskKey = "non-recurring-replace-key";

        // Act - Dispatch NON-recurring task and wait for completion
        var taskId1 = await Dispatcher.Dispatch(new TestTaskRequest("first"), taskKey: taskKey);
        await WaitForTaskStatusAsync(taskId1, QueuedTaskStatus.Completed);

        var tasksAfterFirstRun = await Storage.Get(t => t.Id == taskId1);
        var taskAfterFirstRun = tasksAfterFirstRun.FirstOrDefault();
        taskAfterFirstRun.ShouldNotBeNull();
        var originalCreatedAt = taskAfterFirstRun!.CreatedAtUtc;

        // Dispatch again with same taskKey (should remove old and create new for NON-recurring)
        var taskId2 = await Dispatcher.Dispatch(new TestTaskRequest("second"), taskKey: taskKey);
        await WaitForTaskStatusAsync(taskId2, QueuedTaskStatus.Completed);

        // Assert - Should be DIFFERENT ID (replaced, not updated) for non-recurring tasks
        taskId2.ShouldNotBe(taskId1);

        // Only one task with this TaskKey should exist (filter by TaskKey to avoid flaky test)
        var tasksWithKey = await Storage.Get(t => t.TaskKey == taskKey);
        tasksWithKey.Length.ShouldBe(1);
        tasksWithKey[0].Id.ShouldBe(taskId2);

        // CreatedAtUtc should be different (new task)
        tasksWithKey[0].CreatedAtUtc.ShouldNotBe(originalCreatedAt);
    }

    #endregion

    #region ProcessPendingAsync Simulation - NextRunUtc Rhythm Preservation

    [Fact]
    public async Task RecurringTask_CalculateNextValidRun_MaintainsScheduleRhythm()
    {
        // This test verifies the fix for the schedule drift bug:
        // When CalculateNextValidRun is called with a past scheduledTime,
        // NextRunUtc should maintain the original rhythm (e.g., 5-minute intervals)
        // rather than drifting based on the current time.

        // Arrange
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-22); // 22 minutes ago
        var recurringTask = new EverTask.Scheduler.Recurring.RecurringTask
        {
            MinuteInterval = new EverTask.Scheduler.Recurring.Intervals.MinuteInterval(5)
        };

        // Simulate: task was supposed to run at baseTime+15min (7 minutes ago)
        // but server was down. Now we're recalculating.
        var missedRunTime = baseTime.AddMinutes(15); // 7 minutes ago
        var currentRun = 3; // 3 runs completed

        // Act - Calculate next valid run from the missed time
        var result = recurringTask.CalculateNextValidRun(missedRunTime, currentRun);

        // Assert
        result.NextRun.ShouldNotBeNull();
        // Allow for edge case where nextRun equals now exactly (within tolerance)
        result.NextRun!.Value.ShouldBeGreaterThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(-1));

        // NextRunUtc should maintain 5-minute rhythm from missedRunTime
        // Expected: missedRunTime + 5min, or if that's also past, missedRunTime + 10min, etc.
        var expectedFirstNext = missedRunTime.AddMinutes(5);
        var minutesFromMissed = (result.NextRun.Value - missedRunTime).TotalMinutes;

        // Should be a multiple of 5 minutes from the missed time
        var remainder = minutesFromMissed % 5;
        (remainder < 0.1 || remainder > 4.9).ShouldBeTrue(
            $"NextRun doesn't maintain 5-minute rhythm. " +
            $"Minutes from missedRunTime: {minutesFromMissed}, Remainder: {remainder}");
    }

    [Fact]
    public async Task RecurringTask_WithSpecificRunTime_MaintainsRhythmAfterSkippedOccurrences()
    {
        // This test verifies that when a recurring task with SpecificRunTime
        // has multiple skipped occurrences, the next run maintains the original rhythm.

        // Arrange
        var specificStartTime = DateTimeOffset.UtcNow.AddHours(-1); // Started 1 hour ago
        var recurringTask = new EverTask.Scheduler.Recurring.RecurringTask
        {
            SpecificRunTime = specificStartTime,
            MinuteInterval = new EverTask.Scheduler.Recurring.Intervals.MinuteInterval(5)
        };

        // Simulate: server was down, many runs were missed
        // Last known scheduled time was 30 minutes ago
        var lastScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        var currentRun = 6; // 6 runs completed before downtime

        // Act
        var result = recurringTask.CalculateNextValidRun(lastScheduledTime, currentRun);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Should have skipped some occurrences
        result.SkippedCount.ShouldBeGreaterThan(0);

        // Next run should maintain 5-minute rhythm from the original specific start time
        var minutesFromStart = (result.NextRun.Value - specificStartTime).TotalMinutes;
        var remainder = minutesFromStart % 5;

        (remainder < 0.1 || remainder > 4.9).ShouldBeTrue(
            $"NextRun {result.NextRun.Value:HH:mm:ss.fff} doesn't maintain 5-minute rhythm from start {specificStartTime:HH:mm:ss.fff}. " +
            $"Minutes from start: {minutesFromStart:F2}, Remainder: {remainder:F2}");
    }

    [Fact]
    public async Task Dispatch_RecurringTaskWithTaskKey_AfterRestartSimulation_MaintainsRhythm()
    {
        // This test simulates a restart scenario using the public API:
        // 1. Create recurring task with future execution (so it doesn't run during test)
        // 2. Re-dispatch with same TaskKey (simulating ProcessPendingAsync behavior)
        // 3. Verify NextRunUtc is preserved when still in the future

        // Arrange
        await CreateIsolatedHostAsync();

        // Create a recurring task that runs every hour (won't execute during test)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "restart-rhythm-test-key");

        await Task.Delay(100);

        var initialTasks = await Storage.Get(t => t.Id == taskId);
        var initialTask = initialTasks.FirstOrDefault();
        initialTask.ShouldNotBeNull();

        // Store the original NextRunUtc (should be ~1 hour in the future)
        var originalNextRunUtc = initialTask!.NextRunUtc;
        originalNextRunUtc.ShouldNotBeNull();
        originalNextRunUtc!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Small delay to ensure different timestamp if recalculated
        await Task.Delay(50);

        // Simulate: server restarts and re-dispatches the task
        // The key behavior we're testing: NextRunUtc should be preserved
        // when it's still in the future
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "restart-rhythm-test-key");

        // Assert
        taskId2.ShouldBe(taskId);

        var updatedTasks = await Storage.Get(t => t.Id == taskId);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask.ShouldNotBeNull();

        // NextRunUtc should be preserved (same as before restart)
        // Use tolerance for millisecond differences due to storage precision
        updatedTask!.NextRunUtc.ShouldNotBeNull();
        var timeDiff = Math.Abs((updatedTask.NextRunUtc!.Value - originalNextRunUtc.Value).TotalMilliseconds);
        timeDiff.ShouldBeLessThan(100, $"NextRunUtc should be preserved. Original: {originalNextRunUtc}, Updated: {updatedTask.NextRunUtc}");
    }

    #endregion

    #region Past NextRunUtc Calculation Base Fix

    [Fact]
    public async Task Dispatch_RecurringTaskWithTaskKey_WhenNextRunUtcInPast_UsesItAsCalculationBase_SingleRun()
    {
        // This test verifies the fix for the scenario where:
        // 1. A recurring task exists (created with simple Every interval)
        // 2. NextRunUtc is in the past (app was offline for a short time)
        // 3. CurrentRunCount is 1 (task executed once before offline)
        // 4. On re-dispatch, should use NextRunUtc (not SpecificRunTime) as calculation base

        // Arrange
        await CreateIsolatedHostAsync();

        // Create a recurring task with simple interval (no far-past RunAt)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring
                .Schedule()
                .Every(5).Minutes(),
            taskKey: "past-nextrun-single-runcount-key");

        await Task.Delay(100);

        // Get the initial task and verify NextRunUtc was calculated
        var initialTasks = await Storage.Get(t => t.Id == taskId);
        var initialTask = initialTasks.FirstOrDefault();
        initialTask.ShouldNotBeNull();
        var initialNextRunUtc = initialTask!.NextRunUtc;
        initialNextRunUtc.ShouldNotBeNull();
        initialNextRunUtc!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Simulate one execution and set NextRunUtc to be in the past
        // This simulates: task executed, then app went offline before next run
        var pastNextRunUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        await Storage.UpdateCurrentRun(taskId, 100, pastNextRunUtc, AuditLevel.Full);

        // Set status to WaitingQueue (as if the task is pending re-execution)
        await Storage.SetStatus(taskId, QueuedTaskStatus.WaitingQueue, null, AuditLevel.Full);

        // Verify state before re-dispatch
        var tasksBeforeRedispatch = await Storage.Get(t => t.Id == taskId);
        var taskBeforeRedispatch = tasksBeforeRedispatch.FirstOrDefault();
        taskBeforeRedispatch.ShouldNotBeNull();
        taskBeforeRedispatch!.NextRunUtc.ShouldBe(pastNextRunUtc);
        taskBeforeRedispatch.CurrentRunCount.ShouldBe(1); // One execution

        // Act - Re-dispatch with the same task key
        // This should NOT throw "Invalid scheduler recurring expression"
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring
                .Schedule()
                .Every(5).Minutes(),
            taskKey: "past-nextrun-single-runcount-key");

        // Assert - Should return same ID (updated, not replaced for recurring)
        taskId2.ShouldBe(taskId);

        // Get the updated task
        var updatedTasks = await Storage.Get(t => t.Id == taskId);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask.ShouldNotBeNull();

        // NextRunUtc should be in the future
        updatedTask!.NextRunUtc.ShouldNotBeNull();
        updatedTask.NextRunUtc!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // CurrentRunCount should be preserved (still 1)
        updatedTask.CurrentRunCount.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatch_RecurringTaskWithTaskKey_WhenNextRunUtcInPast_UsesItAsCalculationBase_WithExistingRunCount()
    {
        // This test verifies the same fix but when the task already has multiple executions
        // (CurrentRunCount = 3)

        // Arrange
        await CreateIsolatedHostAsync();

        // Create a recurring task with simple interval
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring
                .Schedule()
                .Every(5).Minutes(),
            taskKey: "past-nextrun-with-runcount-key");

        await Task.Delay(100);

        // Simulate multiple executions by calling UpdateCurrentRun multiple times
        // This increments CurrentRunCount and sets NextRunUtc
        var pastNextRunUtc = DateTimeOffset.UtcNow.AddMinutes(-8);
        await Storage.UpdateCurrentRun(taskId, 100, pastNextRunUtc, AuditLevel.Full); // Run 1
        await Storage.UpdateCurrentRun(taskId, 100, pastNextRunUtc, AuditLevel.Full); // Run 2
        await Storage.UpdateCurrentRun(taskId, 100, pastNextRunUtc, AuditLevel.Full); // Run 3

        // Set status to Completed (simulating last execution completed)
        await Storage.SetStatus(taskId, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        // Verify state before re-dispatch
        var tasksBeforeRedispatch = await Storage.Get(t => t.Id == taskId);
        var taskBeforeRedispatch = tasksBeforeRedispatch.FirstOrDefault();
        taskBeforeRedispatch.ShouldNotBeNull();
        taskBeforeRedispatch!.NextRunUtc.ShouldBe(pastNextRunUtc);
        taskBeforeRedispatch.CurrentRunCount.ShouldBe(3); // 3 executions
        taskBeforeRedispatch.Status.ShouldBe(QueuedTaskStatus.Completed);

        // Act - Re-dispatch with the same task key
        // This should NOT throw "Invalid scheduler recurring expression"
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring
                .Schedule()
                .Every(5).Minutes(),
            taskKey: "past-nextrun-with-runcount-key");

        // Assert - Should return same ID
        taskId2.ShouldBe(taskId);

        // Get the updated task
        var updatedTasks = await Storage.Get(t => t.Id == taskId);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask.ShouldNotBeNull();

        // NextRunUtc should be in the future
        updatedTask!.NextRunUtc.ShouldNotBeNull();
        updatedTask.NextRunUtc!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // CurrentRunCount should be preserved (still 3)
        updatedTask.CurrentRunCount.ShouldBe(3);

        // NextRunUtc should maintain 5-minute rhythm from pastNextRunUtc
        var minutesFromPastNextRun = (updatedTask.NextRunUtc.Value - pastNextRunUtc).TotalMinutes;
        var remainder = minutesFromPastNextRun % 5;

        // Should be a multiple of 5 minutes (allowing small floating point error)
        (remainder < 0.1 || remainder > 4.9).ShouldBeTrue(
            $"NextRunUtc doesn't maintain 5-minute rhythm. " +
            $"pastNextRunUtc: {pastNextRunUtc}, NextRunUtc: {updatedTask.NextRunUtc}, " +
            $"Minutes difference: {minutesFromPastNextRun:F2}, Remainder: {remainder:F2}");
    }

    #endregion
}

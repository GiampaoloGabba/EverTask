using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests verifying that DateTimeOffset values are stored with UTC offset (+00:00).
/// These tests address the bug where LastExecutionUtc was being saved with local timezone offset.
/// </summary>
public class UtcDateTimeOffsetIntegrationTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task CreatedAtUtc_ShouldHaveZeroOffset()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("test"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();

        // CreatedAtUtc should have +00:00 offset
        task!.CreatedAtUtc.Offset.ShouldBe(TimeSpan.Zero,
            $"CreatedAtUtc should have +00:00 offset but has {task.CreatedAtUtc.Offset}");
    }

    [Fact]
    public async Task LastExecutionUtc_ShouldHaveZeroOffset_AfterCompletion()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("test"));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();
        task!.LastExecutionUtc.ShouldNotBeNull();

        // LastExecutionUtc should have +00:00 offset
        task.LastExecutionUtc!.Value.Offset.ShouldBe(TimeSpan.Zero,
            $"LastExecutionUtc should have +00:00 offset but has {task.LastExecutionUtc.Value.Offset}");
    }

    [Fact]
    public async Task ScheduledExecutionUtc_ShouldHaveZeroOffset_WhenScheduled()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("test"), scheduledTime);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();
        task!.ScheduledExecutionUtc.ShouldNotBeNull();

        // ScheduledExecutionUtc should have +00:00 offset
        task.ScheduledExecutionUtc!.Value.Offset.ShouldBe(TimeSpan.Zero,
            $"ScheduledExecutionUtc should have +00:00 offset but has {task.ScheduledExecutionUtc.Value.Offset}");
    }

    [Fact]
    public async Task NextRunUtc_ShouldHaveZeroOffset_ForRecurringTask()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "nextrun-offset-test");

        await Task.Delay(100);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();
        task!.NextRunUtc.ShouldNotBeNull();

        // NextRunUtc should have +00:00 offset
        task.NextRunUtc!.Value.Offset.ShouldBe(TimeSpan.Zero,
            $"NextRunUtc should have +00:00 offset but has {task.NextRunUtc.Value.Offset}");
    }

    [Fact]
    public async Task RecurringTask_AllDateTimeOffsets_ShouldHaveZeroOffset_AfterExecution()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch recurring task that executes quickly
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(10).Seconds(),
            taskKey: "recurring-offset-test");

        // Wait for first execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => StateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 12000);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();

        // All DateTimeOffset fields should have +00:00 offset
        task!.CreatedAtUtc.Offset.ShouldBe(TimeSpan.Zero,
            $"CreatedAtUtc offset mismatch: {task.CreatedAtUtc.Offset}");

        if (task.ScheduledExecutionUtc.HasValue)
        {
            task.ScheduledExecutionUtc.Value.Offset.ShouldBe(TimeSpan.Zero,
                $"ScheduledExecutionUtc offset mismatch: {task.ScheduledExecutionUtc.Value.Offset}");
        }

        if (task.LastExecutionUtc.HasValue)
        {
            task.LastExecutionUtc.Value.Offset.ShouldBe(TimeSpan.Zero,
                $"LastExecutionUtc offset mismatch: {task.LastExecutionUtc.Value.Offset}");
        }

        if (task.NextRunUtc.HasValue)
        {
            task.NextRunUtc.Value.Offset.ShouldBe(TimeSpan.Zero,
                $"NextRunUtc offset mismatch: {task.NextRunUtc.Value.Offset}");
        }
    }

    [Fact]
    public async Task StatusAudit_DateTimes_ShouldHaveZeroOffset()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("test"), auditLevel: AuditLevel.Full);
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();

        // Check status audits
        foreach (var audit in task!.StatusAudits)
        {
            audit.UpdatedAtUtc.Offset.ShouldBe(TimeSpan.Zero,
                $"StatusAudit.UpdatedAtUtc should have +00:00 offset but has {audit.UpdatedAtUtc.Offset}");
        }
    }

    [Fact]
    public async Task RunsAudit_DateTimes_ShouldHaveZeroOffset()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("test"), auditLevel: AuditLevel.Full);
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();

        // Check runs audits
        foreach (var audit in task!.RunsAudits)
        {
            audit.ExecutedAt.Offset.ShouldBe(TimeSpan.Zero,
                $"RunsAudit.ExecutedAt should have +00:00 offset but has {audit.ExecutedAt.Offset}");
        }
    }

    [Fact]
    public async Task DelayedTask_ShouldPreserveUtcOffset_AfterSchedulerTimer()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        var scheduledTime = DateTimeOffset.UtcNow.AddSeconds(2);

        // Act
        var taskId = await Dispatcher.Dispatch(new TestTaskRequest("delayed"), scheduledTime);

        // Wait for task to complete (scheduled + execution)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();

        // All dates should have +00:00 offset even after timer triggered execution
        task!.LastExecutionUtc.ShouldNotBeNull();
        task.LastExecutionUtc!.Value.Offset.ShouldBe(TimeSpan.Zero,
            $"LastExecutionUtc after delayed execution should have +00:00 offset");
    }

    [Fact]
    public async Task FailedTask_LastExecutionUtc_ShouldHaveZeroOffset()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act - Dispatch task that will fail
        var taskId = await Dispatcher.Dispatch(new TestTaskRequestError());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed, timeoutMs: 5000);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();
        task!.LastExecutionUtc.ShouldNotBeNull();

        task.LastExecutionUtc!.Value.Offset.ShouldBe(TimeSpan.Zero,
            $"LastExecutionUtc for failed task should have +00:00 offset but has {task.LastExecutionUtc.Value.Offset}");
    }

    [Fact]
    public async Task CancelledTask_DateTimeFields_ShouldHaveZeroOffset()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Act
        var taskId = await Dispatcher.Dispatch(
            new TestTaskConcurrent1(),
            TimeSpan.FromMinutes(5));

        await Task.Delay(100);
        await Dispatcher.Cancel(taskId);
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Cancelled);

        // Assert
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();

        // CreatedAtUtc should still have +00:00
        task!.CreatedAtUtc.Offset.ShouldBe(TimeSpan.Zero);

        // If LastExecutionUtc is set (shouldn't be for cancelled before execution)
        if (task.LastExecutionUtc.HasValue)
        {
            task.LastExecutionUtc.Value.Offset.ShouldBe(TimeSpan.Zero);
        }
    }
}

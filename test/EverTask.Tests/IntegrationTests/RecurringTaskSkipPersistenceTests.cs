using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests for recurring task skip persistence functionality.
/// Tests that skipped occurrences are properly recorded in the audit trail.
/// </summary>
public class RecurringTaskSkipPersistenceTests : IntegrationTestBase
{
    public RecurringTaskSkipPersistenceTests()
    {
        InitializeHost();
    }
    [Fact]
    public async Task Should_persist_skipped_occurrences_in_RunsAudit()
    {
        // This test verifies that when a recurring task skips missed occurrences,
        // the skip information is persisted in the RunsAudit table

        await StartHostAsync();

        // Create a recurring task that runs every 5 seconds
        var task = new TestTaskDelayed1();
        TestTaskDelayed1.Counter = 0;

        // Dispatch a task scheduled to start in the past (simulating downtime)
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-2); // 2 minutes ago
        var taskId = await Dispatcher!.Dispatch(task, pastTime);

        // Manually update the task to be recurring (simulating a task that was scheduled before downtime)
        var recurringTask = new RecurringTask
        {
            SecondInterval = new SecondInterval(30) // Every 30 seconds
        };

        // Get the task from storage
        var tasks = await Storage!.Get(t => t.Id == taskId);
        tasks.Length.ShouldBe(1);

        var queuedTask = tasks[0];
        queuedTask.IsRecurring = true;
        queuedTask.RecurringTask = Newtonsoft.Json.JsonConvert.SerializeObject(recurringTask);
        queuedTask.NextRunUtc = pastTime;
        queuedTask.ScheduledExecutionUtc = pastTime;

        await Storage.UpdateTask(queuedTask);

        // Manually call the RecordSkippedOccurrences method (simulating what WorkerExecutor does)
        var skippedOccurrences = new List<DateTimeOffset>
        {
            pastTime,
            pastTime.AddSeconds(30),
            pastTime.AddSeconds(60)
        };

        await Storage.RecordSkippedOccurrences(taskId, skippedOccurrences);

        // Verify the skip was recorded
        var updatedTask = await Storage.Get(t => t.Id == taskId);
        updatedTask.Length.ShouldBe(1);

        var runsAudits = updatedTask[0].RunsAudits.ToList();

        // Should have at least one audit entry for the skips
        runsAudits.ShouldNotBeEmpty();

        // Find the skip audit entry
        var skipAudit = runsAudits.FirstOrDefault(a => a.Exception != null && a.Exception.Contains("Skipped"));
        skipAudit.ShouldNotBeNull();
        skipAudit.Exception.ShouldNotBeNull();
        skipAudit.Exception.ShouldContain("Skipped 3 missed occurrence(s)");
        skipAudit.Status.ShouldBe(QueuedTaskStatus.Completed);

        await StopHostAsync();
    }

    [Fact]
    public async Task Should_not_persist_when_no_skips_occurred()
    {
        await StartHostAsync();

        var task = new TestTaskDelayed1();
        var taskId = await Dispatcher!.Dispatch(task);

        // Call RecordSkippedOccurrences with empty list
        var initialTask = await Storage!.Get(t => t.Id == taskId);
        var initialAuditCount = initialTask[0].RunsAudits.Count;

        await Storage.RecordSkippedOccurrences(taskId, new List<DateTimeOffset>());

        // Verify no new audit was added
        var updatedTask = await Storage.Get(t => t.Id == taskId);
        updatedTask[0].RunsAudits.Count.ShouldBe(initialAuditCount);

        await StopHostAsync();
    }

    [Fact]
    public void Extension_method_CalculateNextValidRun_should_return_skip_info()
    {
        // Unit test for the extension method (can run without full integration)

        var recurringTask = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Scheduled 5 hours ago (should skip 5 occurrences)
        var scheduledInPast = DateTimeOffset.UtcNow.AddHours(-5);

        var result = recurringTask.CalculateNextValidRun(scheduledInPast, 1);

        // Should have skipped some occurrences
        result.SkippedCount.ShouldBeGreaterThan(0);
        result.SkippedOccurrences.Count.ShouldBe(result.SkippedCount);
        result.NextRun.ShouldNotBeNull();
        result.NextRun.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // All skipped times should be in the past
        result.SkippedOccurrences.ShouldAllBe(time => time < DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Extension_method_should_handle_null_gracefully()
    {
        RecurringTask? nullTask = null;

        // Should throw ArgumentNullException
        Should.Throw<ArgumentNullException>(() =>
        {
            nullTask!.CalculateNextValidRun(DateTimeOffset.UtcNow, 1);
        });
    }

    [Fact]
    public async Task RecordSkippedOccurrences_should_handle_nonexistent_task()
    {
        await StartHostAsync();

        // Try to record skips for a task that doesn't exist
        var nonExistentTaskId = Guid.NewGuid();
        var skippedOccurrences = new List<DateTimeOffset>
        {
            DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Should not throw, just log a warning
        await Storage!.RecordSkippedOccurrences(nonExistentTaskId, skippedOccurrences);

        // Verify no crash occurred
        var tasks = await Storage.Get(t => t.Id == nonExistentTaskId);
        tasks.Length.ShouldBe(0);

        await StopHostAsync();
    }
}

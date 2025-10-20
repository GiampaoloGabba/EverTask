using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Storage;
using EverTask.Storage.EfCore;
using EverTask.Tests.TestHelpers;
using EverTask.Tests.TestTasks;
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
    [Fact]
    public async Task Should_persist_skipped_occurrences_in_RunsAudit()
    {
        // This test verifies that when a recurring task skips missed occurrences,
        // the skip information is persisted in the RunsAudit table

        await _host.StartAsync();

        // Create a recurring task that runs every 5 seconds
        var task = new TestTaskDelayed1();
        TestTaskDelayed1.Counter = 0;

        // Dispatch a task scheduled to start in the past (simulating downtime)
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-2); // 2 minutes ago
        var taskId = await _dispatcher.Dispatch(task, pastTime);

        // Manually update the task to be recurring (simulating a task that was scheduled before downtime)
        var recurringTask = new RecurringTask
        {
            SecondInterval = new Intervals.SecondInterval(30) // Every 30 seconds
        };

        // Get the task from storage
        var tasks = await _storage.Get(t => t.Id == taskId);
        tasks.Length.ShouldBe(1);

        var queuedTask = tasks[0];
        queuedTask.IsRecurring = true;
        queuedTask.RecurringTask = Newtonsoft.Json.JsonConvert.SerializeObject(recurringTask);
        queuedTask.NextRunUtc = pastTime;
        queuedTask.ScheduledExecutionUtc = pastTime;

        await _storage.UpdateTask(queuedTask);

        // Manually call the RecordSkippedOccurrences method (simulating what WorkerExecutor does)
        if (_storage is EfCoreTaskStorage efCoreStorage)
        {
            var skippedOccurrences = new List<DateTimeOffset>
            {
                pastTime,
                pastTime.AddSeconds(30),
                pastTime.AddSeconds(60)
            };

            await efCoreStorage.RecordSkippedOccurrences(taskId, skippedOccurrences);

            // Verify the skip was recorded
            var updatedTask = await _storage.Get(t => t.Id == taskId);
            updatedTask.Length.ShouldBe(1);

            var runsAudits = updatedTask[0].RunsAudits.ToList();

            // Should have at least one audit entry for the skips
            runsAudits.ShouldNotBeEmpty();

            // Find the skip audit entry
            var skipAudit = runsAudits.FirstOrDefault(a => a.Exception != null && a.Exception.Contains("Skipped"));
            skipAudit.ShouldNotBeNull();
            skipAudit.Exception.ShouldContain("Skipped 3 missed occurrence(s)");
            skipAudit.Status.ShouldBe(QueuedTaskStatus.Completed);
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_not_persist_when_no_skips_occurred()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed1();
        var taskId = await _dispatcher.Dispatch(task);

        // Call RecordSkippedOccurrences with empty list
        if (_storage is EfCoreTaskStorage efCoreStorage)
        {
            var initialTask = await _storage.Get(t => t.Id == taskId);
            var initialAuditCount = initialTask[0].RunsAudits.Count;

            await efCoreStorage.RecordSkippedOccurrences(taskId, new List<DateTimeOffset>());

            // Verify no new audit was added
            var updatedTask = await _storage.Get(t => t.Id == taskId);
            updatedTask[0].RunsAudits.Count.ShouldBe(initialAuditCount);
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Extension_method_CalculateNextValidRun_should_return_skip_info()
    {
        // Unit test for the extension method (can run without full integration)

        var recurringTask = new RecurringTask
        {
            HourInterval = new Intervals.HourInterval(1)
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
        await _host.StartAsync();

        // Try to record skips for a task that doesn't exist
        if (_storage is EfCoreTaskStorage efCoreStorage)
        {
            var nonExistentTaskId = Guid.NewGuid();
            var skippedOccurrences = new List<DateTimeOffset>
            {
                DateTimeOffset.UtcNow.AddMinutes(-5)
            };

            // Should not throw, just log a warning
            await efCoreStorage.RecordSkippedOccurrences(nonExistentTaskId, skippedOccurrences);

            // Verify no crash occurred
            var tasks = await _storage.Get(t => t.Id == nonExistentTaskId);
            tasks.Length.ShouldBe(0);
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        await _host.StopAsync(cts.Token);
    }
}

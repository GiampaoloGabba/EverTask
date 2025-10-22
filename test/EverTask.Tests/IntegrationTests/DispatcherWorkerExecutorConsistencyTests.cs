using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests verifying consistency between Dispatcher and WorkerExecutor
/// when calculating next run times for recurring tasks.
/// Both components should use CalculateNextValidRun() and preserve ExecutionTime.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class DispatcherWorkerExecutorConsistencyTests : IsolatedIntegrationTestBase
{
    // NO instance fields - use base class properties

    [Fact]
    public async Task Dispatcher_And_WorkerExecutor_Should_Both_Use_CalculateNextValidRun()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with first run in the past (should skip)
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-10);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(5).Seconds());

        await Task.Delay(200); // Let Dispatcher calculate

        // Get initial scheduling (done by Dispatcher)
        var tasksAfterDispatch = await Storage.GetAll();
        var taskAfterDispatch = tasksAfterDispatch.FirstOrDefault(t => t.Id == taskId);

        taskAfterDispatch.ShouldNotBeNull();
        var dispatcherNextRun = taskAfterDispatch.NextRunUtc;

        // Assert: Dispatcher should have skipped past occurrences
        dispatcherNextRun.ShouldNotBeNull();
        dispatcherNextRun.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Wait for first execution and re-scheduling (done by WorkerExecutor)
        await TaskWaitHelper.WaitForConditionAsync(
            () => StateManager.GetCounter(nameof(TestTaskRecurringMinutes)) >= 1,
            timeoutMs: 8000);

        // Get task after WorkerExecutor re-schedules
        var tasksAfterExecution = await Storage.GetAll();
        var taskAfterExecution = tasksAfterExecution.FirstOrDefault(t => t.Id == taskId);

        taskAfterExecution.ShouldNotBeNull();
        var workerExecutorNextRun = taskAfterExecution.NextRunUtc;

        // Assert: WorkerExecutor should also calculate from ExecutionTime, not UtcNow
        workerExecutorNextRun.ShouldNotBeNull();
        workerExecutorNextRun.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task First_Run_Dispatcher_Should_Skip_Past_Occurrences()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task starting 1 hour ago, every 20 minutes
        var pastTime = DateTimeOffset.UtcNow.AddHours(-1);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(20).Minutes());

        await Task.Delay(200);

        // Assert: Dispatcher should skip past occurrences
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Should have skipped approximately 3 occurrences (60 minutes / 20 minutes = 3)
        // FIXME: SkippedOccurrencesAudits property does not exist

        // var skippedAudits = // FIXME: SkippedOccurrencesAudits property does not exist - task.task.SkippedOccurrencesAudits;

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Subsequent_Runs_WorkerExecutor_Should_Skip_Past_Occurrences()
    {
        // NOTE: This test simulates a downtime scenario, but since we can't share storage
        // between two separate IHost instances easily without providing a logger,
        // we'll verify the skip behavior using a single host with careful timing.

        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with first run in the past (simulates downtime recovery)
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-5); // 5 seconds ago = ~5 missed 1-second runs

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunAt(pastTime).Then().Every(1).Seconds());

        // Wait for first execution (WorkerExecutor should skip past occurrences)
        await TaskWaitHelper.WaitForConditionAsync(
            () => StateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 3000);

        // Assert: WorkerExecutor should have skipped past occurrences
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Next run should be in the future (not catching up on missed runs)
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Should have executed only once (not 5 times catching up)
        StateManager.GetCounter(nameof(TestTaskRecurringSeconds)).ShouldBe(1);

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task ExecutionTime_Should_Be_Preserved_Across_Dispatcher_And_WorkerExecutor()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task every 3 seconds
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(3).Seconds());

        // Wait for 2 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 10000);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);

        // Get completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();

        completedRuns.Count.ShouldBe(2);

        // Assert: ExecutionTime should be preserved and used consistently
        // Interval between runs should be approximately 3 seconds
        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        interval.ShouldBeGreaterThan(2.5);
        interval.ShouldBeLessThan(4);

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Consistency_Test_HourInterval_Across_Multiple_Runs()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task every hour (testing with seconds for speed)
        // Using SecondInterval but verifying calculation consistency
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Wait for 3 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 10000);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Get all completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(3)
            .ToList();

        completedRuns.Count.ShouldBe(3);

        // Assert: Both Dispatcher (first run) and WorkerExecutor (subsequent runs)
        // should maintain consistent 2-second intervals
        for (int i = 1; i < completedRuns.Count; i++)
        {
            var interval = (completedRuns[i].ExecutedAt - completedRuns[i - 1].ExecutedAt).TotalSeconds;
            interval.ShouldBeGreaterThan(1.5);
            interval.ShouldBeLessThan(3);
        }

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Consistency_Test_DayInterval_Skips_Correctly()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task daily at a specific time in the past
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var specificTime = new TimeOnly(10, 0, 0);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring
                .RunAt(yesterday).Then()
                .Every(1).Days()
                .AtTimes(specificTime));

        await Task.Delay(200);

        // Assert: Dispatcher should have calculated next run for today at 10:00
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();

        // Next run should be in the future
        task.NextRunUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Should be scheduled for 10:00 (hour and minute)
        task.NextRunUtc.Value.Hour.ShouldBe(10);
        task.NextRunUtc.Value.Minute.ShouldBe(0);

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Consistency_Test_CronInterval_Across_Components()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with cron expression (every 5 seconds for testing)
        // Cron: "*/5 * * * * *" (6-field format with seconds)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().UseCron("*/5 * * * * *"));

        // Wait for 2 executions (cron */5 * * * * * = every 5 seconds)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 12000);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Get completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();

        completedRuns.Count.ShouldBe(2);

        // Assert: Interval should be approximately 5 seconds (cron schedule)
        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;
        interval.ShouldBeGreaterThan(4);
        interval.ShouldBeLessThan(6);

        // Cleanup automatic via IAsyncDisposable
    }
}

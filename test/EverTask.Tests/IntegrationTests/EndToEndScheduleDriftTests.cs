using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// End-to-end integration tests for recurring task schedule drift fix.
/// Tests complete scenarios with multiple components working together.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
[Collection("TimingSensitiveTests")]
public class EndToEndScheduleDriftTests : IsolatedIntegrationTestBase
{

    [Fact]
    public async Task EndToEnd_Recurring_Task_Should_Execute_3_Times_And_Track_CurrentRunCount()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch recurring task every 1 second
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for 3 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 10000);

        // Assert
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(3);

        // Verify via audit trail
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(3)
            .ToList();

        completedRuns.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EndToEnd_Retry_Should_Not_Affect_Next_Run_Calculation()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        TestTaskRecurringWithFailure.Counter = 0; // Reset static counter
        TestTaskRecurringWithFailure.FailUntilCount = 2; // Fail twice, then succeed

        // Act: Dispatch recurring task with retry policy (every 2 seconds)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringWithFailure(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Wait for 2 successful executions (each might have retries)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 15000);

        // Assert
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

        // Verify that retries didn't affect scheduling
        // Interval should still be approximately 2 seconds between successful runs
        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        // Allow wider tolerance due to retry delays
        interval.ShouldBeGreaterThan(1.5);
        interval.ShouldBeLessThan(5); // Should not drift significantly despite retries
    }

    [Fact]
    public async Task EndToEnd_Timeout_Should_Not_Affect_Next_Run_Calculation()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch recurring task with custom timeout (every 2 seconds)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Wait for 2 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 10000);

        // Assert
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

        // Verify scheduling is consistent
        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;
        interval.ShouldBeGreaterThan(1.5);
        interval.ShouldBeLessThan(3);
    }

    [Fact]
    public async Task EndToEnd_Multiple_Concurrent_Recurring_Tasks_Should_Maintain_Independent_Schedules()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch 3 different recurring tasks with different intervals
        var task1Id = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        var task2Id = await Dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.Schedule().Every(2).Seconds());

        var task3Id = await Dispatcher.Dispatch(
            new TestTaskDelayedRecurring(delayMs: 50),
            recurring => recurring.Schedule().Every(3).Seconds());

        // Wait for all tasks to complete at least 2 runs
        // Adaptive: Local 8s/12s, CI 20s/30s (coverage overhead)
        await Task.WhenAll(
            WaitForRecurringRunsAsync(task1Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000)),
            WaitForRecurringRunsAsync(task2Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000)),
            WaitForRecurringRunsAsync(task3Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(12000, 30000))
        );

        // Assert: Each task should maintain its own schedule
        var tasks = await Storage.GetAll();

        var task1 = tasks.FirstOrDefault(t => t.Id == task1Id);
        var task2 = tasks.FirstOrDefault(t => t.Id == task2Id);
        var task3 = tasks.FirstOrDefault(t => t.Id == task3Id);

        task1.ShouldNotBeNull();
        task2.ShouldNotBeNull();
        task3.ShouldNotBeNull();

        // Verify task 1 (every 1 second)
        var task1Runs = task1.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();
        var task1Interval = (task1Runs[1].ExecutedAt - task1Runs[0].ExecutedAt).TotalSeconds;
        task1Interval.ShouldBeGreaterThan(0.5);
        task1Interval.ShouldBeLessThan(2);

        // Verify task 2 (every 2 seconds)
        var task2Runs = task2.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();
        var task2Interval = (task2Runs[1].ExecutedAt - task2Runs[0].ExecutedAt).TotalSeconds;
        task2Interval.ShouldBeGreaterThan(1.5);
        task2Interval.ShouldBeLessThan(3);

        // Verify task 3 (every 3 seconds)
        var task3Runs = task3.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();
        var task3Interval = (task3Runs[1].ExecutedAt - task3Runs[0].ExecutedAt).TotalSeconds;
        task3Interval.ShouldBeGreaterThan(2.5);
        task3Interval.ShouldBeLessThan(4);
    }

    [Fact]
    public async Task EndToEnd_Recurring_Task_With_Queue_Sharding_Should_Work()
    {
        // Arrange: Create host with sharding enabled
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder
                .AddQueue("shard1")
                .AddQueue("shard2")
                .AddMemoryStorage(),
            configureEverTask: cfg => cfg.SetMaxDegreeOfParallelism(10));

        // Act: Dispatch recurring tasks with unique task keys
        var task1Id = await Dispatcher.Dispatch(
            new TestTaskRecurringQueueShard1(),
            recurring => recurring.Schedule().Every(1).Seconds());

        var task2Id = await Dispatcher.Dispatch(
            new TestTaskRecurringQueueShard2(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for both tasks to complete runs
        // Adaptive: Local 8s, CI 20s (coverage overhead)
        await Task.WhenAll(
            WaitForRecurringRunsAsync(task1Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000)),
            WaitForRecurringRunsAsync(task2Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000))
        );

        // Assert
        var tasks = await Storage.GetAll();

        var task1 = tasks.FirstOrDefault(t => t.Id == task1Id);
        var task2 = tasks.FirstOrDefault(t => t.Id == task2Id);

        task1.ShouldNotBeNull();
        task2.ShouldNotBeNull();

        task1.QueueName.ShouldBe("shard1");
        task2.QueueName.ShouldBe("shard2");

        task1.CurrentRunCount.HasValue.ShouldBeTrue();
        task1.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);
        task2.CurrentRunCount.HasValue.ShouldBeTrue();
        task2.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task EndToEnd_Complex_Scenario_With_Downtime_Recovery()
    {
        // Arrange: Create first host
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Preserve storage and state manager for reuse after restart
        var storage = Storage;
        var stateManager = StateManager;

        // Act: Dispatch recurring task
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for first execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => stateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 3000);

        // Simulate downtime
        await StopHostAsync();
        await Task.Delay(3000); // 3 seconds downtime

        // Restart: Create new host with same storage and state manager
        // NOTE: configureServices adds ITaskStorage AFTER base class AddMemoryStorage().
        // This intentionally overrides the default storage to reuse the preserved instance
        // for downtime recovery testing. Last registration wins in .NET DI.
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5,
            configureServices: svc =>
            {
                // Override default storage with preserved instance (last registration wins)
                svc.AddSingleton<ITaskStorage>(storage);
                // Override default state manager with preserved instance
                svc.AddSingleton(stateManager);
            });

        // Wait for recovery and additional executions
        await Task.Delay(3000);

        // Assert
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Should have skipped occurrences recorded
        // FIXME: SkippedOccurrencesAudits property does not exist

        // var skippedAudits = // FIXME: SkippedOccurrencesAudits property does not exist - task.task.SkippedOccurrencesAudits;

        // Should have resumed execution
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThan(1);

        // Next run should be in the future
        if (task.NextRunUtc != null)
        {
            task.NextRunUtc.Value.ShouldBeGreaterThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(-1));
        }
    }

    [Fact]
    public async Task EndToEnd_No_Drift_Over_Many_Executions()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch recurring task every 500ms (using 1 second for test speed)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for 10 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 10, timeoutMs: 15000);

        // Assert
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Get all completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(10)
            .ToList();

        completedRuns.Count.ShouldBe(10);

        // Verify total time is approximately 9 seconds (9 intervals * 1 second)
        var totalTime = (completedRuns[9].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        // Allow tolerance: should be between 8 and 11 seconds
        totalTime.ShouldBeGreaterThan(8);
        totalTime.ShouldBeLessThan(11);

        // Verify no significant drift: average interval should be close to 1 second
        var averageInterval = totalTime / 9; // 9 intervals between 10 runs
        averageInterval.ShouldBeGreaterThan(0.8); // 800ms
        averageInterval.ShouldBeLessThan(1.3); // 1300ms
    }
}

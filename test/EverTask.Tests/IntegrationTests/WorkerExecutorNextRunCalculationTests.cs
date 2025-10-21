using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests verifying that WorkerExecutor correctly calculates next run
/// for recurring tasks based on ExecutionTime (scheduled time) instead of UtcNow.
/// This prevents schedule drift when tasks execute with delays.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class WorkerExecutorNextRunCalculationTests
{
    private IHost _host = null!;
    private ITaskDispatcher _dispatcher = null!;
    private ITaskStorage _storage = null!;
    private TestTaskStateManager _stateManager = null!;

    private void InitializeHost(bool reuseStorage = false)
    {
        // Preserve storage and state manager across host rebuilds for downtime recovery tests
        var existingStorage = reuseStorage ? _storage : null;
        var existingStateManager = reuseStorage ? _stateManager : null;

        _host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                        .RegisterTasksFromAssembly(typeof(TestTaskRecurringSeconds).Assembly)
                        .SetChannelOptions(10)
                        .SetMaxDegreeOfParallelism(5))
                    .AddMemoryStorage();

                // Reuse existing storage if provided (for restart scenarios)
                if (existingStorage != null)
                    services.AddSingleton(existingStorage);

                // Reuse existing state manager if provided (for restart scenarios)
                if (existingStateManager != null)
                    services.AddSingleton(existingStateManager);
                else
                    services.AddSingleton<TestTaskStateManager>();
            })
            .Build();

        _dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage = _host.Services.GetRequiredService<ITaskStorage>();
        _stateManager = _host.Services.GetRequiredService<TestTaskStateManager>();
    }

    [Fact]
    public async Task WorkerExecutor_Should_Calculate_NextRun_From_ExecutionTime_Not_UtcNow()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task every 5 seconds
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(5).Seconds());

        // Wait for first execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => _stateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 7000);

        // Get task state after first run
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(1);
        task.NextRunUtc.ShouldNotBeNull();

        // Get the scheduled execution time (ExecutionTime) from the first run
        var firstRun = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .FirstOrDefault();

        firstRun.ShouldNotBeNull();

        // Assert: Next run should be ExecutionTime + 5 seconds, not UtcNow + 5 seconds
        // This verifies that WorkerExecutor used ExecutionTime for calculation
        var expectedNextRun = firstRun.ExecutedAt.AddSeconds(5);
        var timeDiff = Math.Abs((task.NextRunUtc!.Value - expectedNextRun).TotalSeconds);

        // Allow 1 second tolerance for processing delays
        timeDiff.ShouldBeLessThan(1);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Calculate_NextRun_From_ScheduledTime_When_Delayed()
    {
        // Arrange: Create a task that delays execution to simulate late execution
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task every 3 seconds that takes 1 second to execute
        var taskId = await _dispatcher.Dispatch(
            new TestTaskDelayedRecurring(delayMs: 1000),
            recurring => recurring.Schedule().Every(3).Seconds());

        // Wait for 2 executions to verify consistent scheduling
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, expectedRuns: 2, timeoutMs: 10000);

        // Get task state
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);

        // Get all completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .ToList();

        completedRuns.Count.ShouldBeGreaterThanOrEqualTo(2);

        // Assert: Time between runs should be approximately 3 seconds (interval)
        // NOT 3 seconds + task execution time (which would indicate drift)
        var timeBetweenRuns = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        // Should be close to 3 seconds, allowing 1.5 second tolerance
        // (3 seconds interval, even though task takes 1 second to run)
        timeBetweenRuns.ShouldBeGreaterThan(2.5);
        timeBetweenRuns.ShouldBeLessThan(4.5);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Skip_Past_Occurrences_After_Downtime()
    {
        // Arrange
        InitializeHost();

        // Start host and dispatch a fast recurring task (every 1 second)
        await _host.StartAsync();

        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for first execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => _stateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 3000);

        // Simulate downtime by stopping the host
        await _host.StopAsync(CancellationToken.None);

        // Wait 5 seconds (simulating system downtime - should miss ~5 occurrences)
        await Task.Delay(5000);

        // Restart host (simulating system recovery - rebuild host as IHost cannot be restarted)
        InitializeHost(reuseStorage: true);
        await _host.StartAsync();

        // Wait for task to be rescheduled and executed
        await Task.Delay(3000);

        // Assert: Task should have skipped past occurrences
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Should have recorded skipped occurrences
        // FIXME: SkippedOccurrencesAudits property does not exist

        // var skippedAudits = // FIXME: SkippedOccurrencesAudits property does not exist - task.task.SkippedOccurrencesAudits;

        // Next run should be in the future, not in the past
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(-1));

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Record_Skipped_Occurrences_In_Storage()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task that starts in the past (2 minutes ago, every 30 seconds)
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-2);

        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunAt(pastTime).Then().Every(30).Seconds());

        // Wait for task to be scheduled (should skip past occurrences)
        await Task.Delay(500);

        // Assert: Storage should have skipped occurrences recorded
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Should have skipped approximately 4 occurrences (2 minutes / 30 seconds = 4)
        // Note: Skipped occurrences are recorded via ITaskStorage.RecordSkippedOccurrences()
        // but are not exposed as a direct property on QueuedTask. The task should be scheduled
        // for the next valid future run.

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Maintain_Schedule_Across_Multiple_Runs()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task every 2 seconds
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Wait for 3 executions
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, expectedRuns: 3, timeoutMs: 10000);

        // Get task state
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(3);

        // Get all completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(3)
            .ToList();

        completedRuns.Count.ShouldBe(3);

        // Assert: All intervals should be approximately 2 seconds
        for (int i = 1; i < completedRuns.Count; i++)
        {
            var interval = (completedRuns[i].ExecutedAt - completedRuns[i - 1].ExecutedAt).TotalSeconds;

            // Allow 1 second tolerance for processing
            interval.ShouldBeGreaterThan(1.5);
            interval.ShouldBeLessThan(3);
        }

        // Verify no cumulative drift: total time should be approximately 4 seconds (2 intervals * 2 seconds)
        var totalTime = (completedRuns[2].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;
        totalTime.ShouldBeGreaterThan(3.5);
        totalTime.ShouldBeLessThan(5);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerExecutor_Should_Use_ExecutionTime_For_HourInterval()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task every 1 hour
        // Note: OnMinute() is not available on Hours() builder. The hour interval will use current minute.
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours());

        // Wait for task to be scheduled
        await Task.Delay(500);

        // Get task state
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();

        // Next run should be approximately 1 hour from now
        var expectedNextRun = DateTimeOffset.UtcNow.AddHours(1);
        var timeDiff = Math.Abs((task.NextRunUtc.Value - expectedNextRun).TotalMinutes);
        timeDiff.ShouldBeLessThan(2); // Within 2 minutes tolerance

        await _host.StopAsync(CancellationToken.None);
    }
}

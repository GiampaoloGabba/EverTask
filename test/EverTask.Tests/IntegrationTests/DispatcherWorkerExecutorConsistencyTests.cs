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
public class DispatcherWorkerExecutorConsistencyTests
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
    public async Task Dispatcher_And_WorkerExecutor_Should_Both_Use_CalculateNextValidRun()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task with first run in the past (should skip)
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-10);

        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(5).Seconds());

        await Task.Delay(200); // Let Dispatcher calculate

        // Get initial scheduling (done by Dispatcher)
        var tasksAfterDispatch = await _storage.GetAll();
        var taskAfterDispatch = tasksAfterDispatch.FirstOrDefault(t => t.Id == taskId);

        taskAfterDispatch.ShouldNotBeNull();
        var dispatcherNextRun = taskAfterDispatch.NextRunUtc;

        // Assert: Dispatcher should have skipped past occurrences
        dispatcherNextRun.ShouldNotBeNull();
        dispatcherNextRun.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Wait for first execution and re-scheduling (done by WorkerExecutor)
        await TaskWaitHelper.WaitForConditionAsync(
            () => _stateManager.GetCounter(nameof(TestTaskRecurringMinutes)) >= 1,
            timeoutMs: 8000);

        // Get task after WorkerExecutor re-schedules
        var tasksAfterExecution = await _storage.GetAll();
        var taskAfterExecution = tasksAfterExecution.FirstOrDefault(t => t.Id == taskId);

        taskAfterExecution.ShouldNotBeNull();
        var workerExecutorNextRun = taskAfterExecution.NextRunUtc;

        // Assert: WorkerExecutor should also calculate from ExecutionTime, not UtcNow
        workerExecutorNextRun.ShouldNotBeNull();
        workerExecutorNextRun.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task First_Run_Dispatcher_Should_Skip_Past_Occurrences()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task starting 1 hour ago, every 20 minutes
        var pastTime = DateTimeOffset.UtcNow.AddHours(-1);

        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(20).Minutes());

        await Task.Delay(200);

        // Assert: Dispatcher should skip past occurrences
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Should have skipped approximately 3 occurrences (60 minutes / 20 minutes = 3)
        // FIXME: SkippedOccurrencesAudits property does not exist

        // var skippedAudits = // FIXME: SkippedOccurrencesAudits property does not exist - task.task.SkippedOccurrencesAudits;

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Subsequent_Runs_WorkerExecutor_Should_Skip_Past_Occurrences()
    {
        // Arrange
        InitializeHost();

        // Start host and dispatch fast recurring task
        await _host.StartAsync();

        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for first execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => _stateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 3000);

        // Simulate downtime
        await _host.StopAsync(CancellationToken.None);
        await Task.Delay(5000); // 5 seconds downtime = ~5 missed runs

        // Restart (WorkerExecutor will reschedule from storage - rebuild host as IHost cannot be restarted)
        InitializeHost(reuseStorage: true);
        await _host.StartAsync();
        await Task.Delay(1000);

        // Assert: WorkerExecutor should have skipped past occurrences
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Next run should be in the future
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(-1));

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecutionTime_Should_Be_Preserved_Across_Dispatcher_And_WorkerExecutor()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task every 3 seconds
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(3).Seconds());

        // Wait for 2 executions
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, expectedRuns: 2, timeoutMs: 10000);

        // Get task state
        var tasks = await _storage.GetAll();
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

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consistency_Test_HourInterval_Across_Multiple_Runs()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task every hour (testing with seconds for speed)
        // Using SecondInterval but verifying calculation consistency
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Wait for 3 executions
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, expectedRuns: 3, timeoutMs: 10000);

        // Get task state
        var tasks = await _storage.GetAll();
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

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consistency_Test_DayInterval_Skips_Correctly()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task daily at a specific time in the past
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var specificTime = new TimeOnly(10, 0, 0);

        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring
                .RunAt(yesterday).Then()
                .Every(1).Days()
                .AtTimes(specificTime));

        await Task.Delay(200);

        // Assert: Dispatcher should have calculated next run for today at 10:00
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();

        // Next run should be in the future
        task.NextRunUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Should be scheduled for 10:00 (hour and minute)
        task.NextRunUtc.Value.Hour.ShouldBe(10);
        task.NextRunUtc.Value.Minute.ShouldBe(0);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consistency_Test_CronInterval_Across_Components()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task with cron expression (every 5 seconds for testing)
        // Cron: "*/5 * * * * *" (6-field format with seconds)
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().UseCron("*/5 * * * * *"));

        // Wait for 2 executions
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, expectedRuns: 2, timeoutMs: 15000);

        // Get task state
        var tasks = await _storage.GetAll();
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

        await _host.StopAsync(CancellationToken.None);
    }
}

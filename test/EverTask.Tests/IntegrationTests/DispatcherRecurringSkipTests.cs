using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests verifying that Dispatcher correctly handles recurring tasks
/// with past scheduled times by skipping to the next valid run.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class DispatcherRecurringSkipTests
{
    private IHost _host = null!;
    private ITaskDispatcher _dispatcher = null!;
    private ITaskStorage _storage = null!;
    private TestTaskStateManager _stateManager = null!;

    private void InitializeHost()
    {
        _host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                        .RegisterTasksFromAssembly(typeof(TestTaskRecurringSeconds).Assembly)
                        .SetChannelOptions(10)
                        .SetMaxDegreeOfParallelism(5))
                    .AddMemoryStorage();

                services.AddSingleton<TestTaskStateManager>();
            })
            .Build();

        _dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage = _host.Services.GetRequiredService<ITaskStorage>();
        _stateManager = _host.Services.GetRequiredService<TestTaskStateManager>();
    }

    [Fact]
    public async Task Dispatcher_Should_Not_Skip_When_FirstRun_InFuture()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        var futureTime = DateTimeOffset.UtcNow.AddMinutes(5);

        // Act: Dispatch recurring task with first run in the future
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunAt(futureTime).Then().Every(1).Seconds());

        await Task.Delay(100); // Give dispatcher time to schedule

        // Assert: Task should be scheduled for the future time, not skipped
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.Status.ShouldBe(QueuedTaskStatus.WaitingQueue); // Future tasks stay in WaitingQueue until timer triggers
        task.ScheduledExecutionUtc.ShouldNotBeNull();

        // Should be scheduled for approximately the specified time (within 1 second tolerance)
        var timeDiff = Math.Abs((task.ScheduledExecutionUtc!.Value - futureTime).TotalSeconds);
        timeDiff.ShouldBeLessThan(1);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_Should_Skip_When_FirstRun_InPast()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        var pastTime = DateTimeOffset.UtcNow.AddHours(-2);

        // Act: Dispatch recurring task with first run 2 hours in the past (every 30 minutes)
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(30).Minutes());

        await Task.Delay(100); // Give dispatcher time to calculate and schedule

        // Assert: Task should skip past occurrences and schedule for next future run
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.Status.ShouldBe(QueuedTaskStatus.WaitingQueue); // Scheduled for future, waiting for timer
        task.NextRunUtc.ShouldNotBeNull();

        // Next run should be in the future
        task.NextRunUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Should have skipped occurrences - the RecordSkippedOccurrences method should have been called
        // Note: We can't directly verify skipped occurrences as they're not exposed as a property,
        // but the task should be scheduled for a future run
        // The implementation calls ITaskStorage.RecordSkippedOccurrences() which creates audit entries

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_Should_Execute_Immediately_With_RunNow()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Act: Dispatch recurring task with RunNow
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunNow().Then().Every(5).Seconds());

        // Wait for first execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => _stateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 3000);

        // Assert: Task should have been queued immediately and executed
        var counter = _stateManager.GetCounter(nameof(TestTaskRecurringSeconds));
        counter.ShouldBeGreaterThanOrEqualTo(1);

        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(1);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_Should_Respect_InitialDelay()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        var initialDelay = TimeSpan.FromSeconds(2);
        var startTime = DateTimeOffset.UtcNow;

        // Act: Dispatch recurring task with InitialDelay
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunDelayed(initialDelay).Then().Every(1).Seconds());

        // Wait for first execution (should happen after initial delay)
        await TaskWaitHelper.WaitForConditionAsync(
            () => _stateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 4000);

        var executionTime = DateTimeOffset.UtcNow;

        // Assert: First execution should have happened after initial delay
        var elapsedTime = executionTime - startTime;
        elapsedTime.TotalSeconds.ShouldBeGreaterThanOrEqualTo(initialDelay.TotalSeconds - 0.5); // 0.5s tolerance

        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(1);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_Should_Skip_SpecificRunTime_InPast()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-30);

        // Act: Dispatch recurring task with SpecificRunTime in the past (every 10 minutes)
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(10).Minutes());

        await Task.Delay(100); // Give dispatcher time to calculate

        // Assert: Task should skip to next valid run (in the future)
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.Status.ShouldBe(QueuedTaskStatus.WaitingQueue); // Scheduled for future, waiting for timer
        task.ScheduledExecutionUtc.ShouldNotBeNull();
        task.ScheduledExecutionUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_Should_Execute_OneTimeTask_ScheduledInPast_Immediately()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-10);

        // Act: Dispatch one-time (non-recurring) task scheduled in the past
        var taskId = await _dispatcher.Dispatch(new TestTaskConcurrent1(), pastTime);

        // Wait for execution
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);

        // Assert: One-time task should execute immediately (behavior unchanged)
        var tasks = await _storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        // Note: CurrentRunCount is only tracked for recurring tasks, not one-time tasks

        await _host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_Should_Calculate_NextRun_From_ScheduledTime_Not_UtcNow()
    {
        // Arrange
        InitializeHost();
        await _host.StartAsync();

        // Dispatch recurring task every 10 seconds
        var taskId = await _dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(10).Seconds());

        // Wait for first execution
        await TaskWaitHelper.WaitForConditionAsync(
            () => _stateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1,
            timeoutMs: 12000);

        // Get task after first run
        var tasksAfterFirstRun = await _storage.GetAll();
        var taskAfterFirstRun = tasksAfterFirstRun.FirstOrDefault(t => t.Id == taskId);

        taskAfterFirstRun.ShouldNotBeNull();
        var firstNextRun = taskAfterFirstRun.NextRunUtc;
        firstNextRun.ShouldNotBeNull();

        // Get the last execution time from audits
        var lastExecution = taskAfterFirstRun.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderByDescending(a => a.ExecutedAt)
            .FirstOrDefault();

        lastExecution.ShouldNotBeNull();

        // Assert: Next run should be calculated from the ExecutionTime (scheduled time),
        // not from UtcNow. The difference should be approximately 10 seconds.
        var expectedNextRun = lastExecution.ExecutedAt.AddSeconds(10);
        var timeDiff = Math.Abs((firstNextRun.Value - expectedNextRun).TotalSeconds);

        // Allow 2 second tolerance for execution delays
        timeDiff.ShouldBeLessThan(2);

        await _host.StopAsync(CancellationToken.None);
    }
}

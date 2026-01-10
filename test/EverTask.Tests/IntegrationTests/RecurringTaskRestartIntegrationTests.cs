using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Comprehensive integration tests for recurring task behavior during app restart scenarios.
/// Tests verify schedule rhythm preservation, NextRunUtc handling, and history preservation.
/// These tests address the bugs fixed in the schedule drift fix.
/// </summary>
public class RecurringTaskRestartIntegrationTests : IsolatedIntegrationTestBase
{
    #region Schedule Rhythm Preservation Tests

    [Theory]
    [InlineData(5)]    // Every 5 seconds
    [InlineData(10)]   // Every 10 seconds
    [InlineData(30)]   // Every 30 seconds
    public async Task SecondInterval_ShouldMaintainRhythm_WhenCalculatingNextRun(int intervalSeconds)
    {
        // This unit test verifies that CalculateNextValidRun maintains rhythm for second intervals
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var recurringTask = new RecurringTask
        {
            SecondInterval = new SecondInterval(intervalSeconds)
        };

        var result = recurringTask.CalculateNextValidRun(baseTime, 10);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Verify rhythm: seconds from base should be a multiple of interval
        var secondsFromBase = (result.NextRun.Value - baseTime).TotalSeconds;
        var remainder = secondsFromBase % intervalSeconds;

        (remainder < 0.5 || remainder > intervalSeconds - 0.5).ShouldBeTrue(
            $"Expected rhythm of {intervalSeconds}s from {baseTime:HH:mm:ss}. " +
            $"Next run: {result.NextRun.Value:HH:mm:ss}, Seconds from base: {secondsFromBase:F1}, Remainder: {remainder:F1}");
    }

    [Theory]
    [InlineData(1)]    // Every minute
    [InlineData(5)]    // Every 5 minutes
    [InlineData(15)]   // Every 15 minutes
    [InlineData(30)]   // Every 30 minutes
    public async Task MinuteInterval_ShouldMaintainRhythm_WhenCalculatingNextRun(int intervalMinutes)
    {
        // Simulate: task started 2 hours ago
        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(intervalMinutes)
        };

        var result = recurringTask.CalculateNextValidRun(baseTime, 10);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Verify rhythm
        var minutesFromBase = (result.NextRun.Value - baseTime).TotalMinutes;
        var remainder = minutesFromBase % intervalMinutes;

        (remainder < 0.1 || remainder > intervalMinutes - 0.1).ShouldBeTrue(
            $"Expected rhythm of {intervalMinutes}min from {baseTime:HH:mm}. " +
            $"Next run: {result.NextRun.Value:HH:mm}, Minutes from base: {minutesFromBase:F1}, Remainder: {remainder:F1}");
    }

    [Theory]
    [InlineData(1)]   // Every hour
    [InlineData(2)]   // Every 2 hours
    [InlineData(6)]   // Every 6 hours
    [InlineData(12)]  // Every 12 hours
    public async Task HourInterval_ShouldMaintainRhythm_WhenCalculatingNextRun(int intervalHours)
    {
        // Simulate: task started 24 hours ago
        var baseTime = DateTimeOffset.UtcNow.AddHours(-24);
        var recurringTask = new RecurringTask
        {
            HourInterval = new HourInterval(intervalHours)
        };

        var result = recurringTask.CalculateNextValidRun(baseTime, 5);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Verify rhythm
        var hoursFromBase = (result.NextRun.Value - baseTime).TotalHours;
        var remainder = hoursFromBase % intervalHours;

        (remainder < 0.1 || remainder > intervalHours - 0.1).ShouldBeTrue(
            $"Expected rhythm of {intervalHours}h from {baseTime:HH:mm}. " +
            $"Next run: {result.NextRun.Value:HH:mm}, Hours from base: {hoursFromBase:F1}, Remainder: {remainder:F1}");
    }

    [Fact]
    public async Task DayInterval_ShouldMaintainRhythm_WhenCalculatingNextRun()
    {
        // Task runs daily at 09:00
        var baseTime = new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var recurringTask = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = new[] { new TimeOnly(9, 0) } }
        };

        // Simulate: server was down for 3 days, now it's Jan 4 at 10:00
        var now = new DateTimeOffset(2024, 1, 4, 10, 0, 0, TimeSpan.Zero);

        // Calculate from last scheduled time (Jan 3 at 09:00)
        var lastScheduled = new DateTimeOffset(2024, 1, 3, 9, 0, 0, TimeSpan.Zero);
        var result = recurringTask.CalculateNextValidRun(lastScheduled, 3, referenceTime: now);

        result.NextRun.ShouldNotBeNull();

        // Should skip Jan 4 09:00 (in past) and schedule for Jan 5 09:00
        result.NextRun!.Value.Day.ShouldBe(5);
        result.NextRun.Value.Hour.ShouldBe(9);
        result.NextRun.Value.Minute.ShouldBe(0);
    }

    [Theory]
    [InlineData("0 * * * *", 60)]      // Every hour at :00
    [InlineData("*/15 * * * *", 15)]   // Every 15 minutes
    [InlineData("0 0 * * *", 1440)]    // Daily at midnight (1440 minutes = 24 hours)
    public async Task CronInterval_ShouldCalculateNextRun_FromScheduledTime(string cronExpression, int expectedMinuteInterval)
    {
        var recurringTask = new RecurringTask
        {
            CronInterval = new CronInterval(cronExpression)
        };

        // Base time in the past
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-expectedMinuteInterval * 3);

        var result = recurringTask.CalculateNextValidRun(baseTime, 5);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        result.SkippedCount.ShouldBeGreaterThan(0); // Should have skipped some occurrences
    }

    #endregion

    #region MaxRuns and RunUntil Constraint Tests

    [Fact]
    public async Task RecurringTask_ShouldRespectMaxRuns_AfterRestart()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours().AtMinute(0).MaxRuns(5), // Max 5 runs
            taskKey: "maxruns-restart-key");

        await Task.Delay(100);

        // Get task and manually set CurrentRunCount close to max
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task.ShouldNotBeNull();

        // Update to simulate 4 runs completed
        task!.CurrentRunCount = 4;
        await Storage.UpdateTask(task);

        // Verify task state
        var updatedTasks = await Storage.Get(t => t.Id == taskId);
        updatedTasks.FirstOrDefault()!.CurrentRunCount.ShouldBe(4);

        // Simulate restart - re-dispatch with same TaskKey
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours().AtMinute(0).MaxRuns(5),
            taskKey: "maxruns-restart-key");

        // Assert
        taskId2.ShouldBe(taskId);

        // CurrentRunCount should be preserved
        var afterRestartTasks = await Storage.Get(t => t.Id == taskId);
        afterRestartTasks.FirstOrDefault()!.CurrentRunCount.ShouldBe(4);
    }

    [Fact]
    public async Task RecurringTask_ShouldReturnNull_WhenMaxRunsReached()
    {
        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5),
            MaxRuns = 10
        };

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);

        // Try to calculate when already at max runs
        var result = recurringTask.CalculateNextRun(baseTime, 10);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RecurringTask_ShouldRespectRunUntil_WhenCalculatingNextRun()
    {
        var endTime = DateTimeOffset.UtcNow.AddMinutes(30);
        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10),
            RunUntil = endTime
        };

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-60);
        var result = recurringTask.CalculateNextValidRun(baseTime, 5);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeLessThanOrEqualTo(endTime);
    }

    [Fact]
    public async Task RecurringTask_ShouldReturnNull_WhenRunUntilPassed()
    {
        // RunUntil is checked against the *next calculated time*, not the baseTime
        // So we need a scenario where the calculated next time exceeds RunUntil
        var futureTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var runUntilTime = DateTimeOffset.UtcNow.AddMinutes(2); // Expires before next run

        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10),
            RunUntil = runUntilTime
        };

        // Next run would be now + 10min, which exceeds runUntilTime (now + 2min)
        var result = recurringTask.CalculateNextRun(DateTimeOffset.UtcNow, 1);

        result.ShouldBeNull();
    }

    #endregion

    #region Multiple Restart Simulation Tests

    [Fact]
    public async Task RecurringTask_ShouldPreserveState_AcrossMultipleRestarts()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // First dispatch
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "multi-restart-key");

        await Task.Delay(100);

        var initialTasks = await Storage.Get(t => t.Id == taskId);
        var initialTask = initialTasks.FirstOrDefault();
        initialTask.ShouldNotBeNull();
        var originalCreatedAt = initialTask!.CreatedAtUtc;
        var originalNextRunUtc = initialTask.NextRunUtc;

        // Simulate first restart
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "multi-restart-key");

        taskId2.ShouldBe(taskId);

        // Simulate second restart
        var taskId3 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "multi-restart-key");

        taskId3.ShouldBe(taskId);

        // Simulate third restart
        var taskId4 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "multi-restart-key");

        // Assert - All restarts should return same ID
        taskId4.ShouldBe(taskId);

        // State should be preserved
        var finalTasks = await Storage.Get(t => t.Id == taskId);
        var finalTask = finalTasks.FirstOrDefault();
        finalTask.ShouldNotBeNull();
        finalTask!.CreatedAtUtc.ShouldBe(originalCreatedAt);

        // NextRunUtc should be preserved (use tolerance for precision)
        var timeDiff = Math.Abs((finalTask.NextRunUtc!.Value - originalNextRunUtc!.Value).TotalMilliseconds);
        timeDiff.ShouldBeLessThan(100);

        // Only one task should exist
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(1);
    }

    [Fact]
    public async Task RecurringTask_ShouldHandleStateChanges_BetweenRestarts()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Create task
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "state-change-restart-key");

        await Task.Delay(100);

        // Manually update CurrentRunCount (simulating execution)
        var tasks = await Storage.Get(t => t.Id == taskId);
        var task = tasks.FirstOrDefault();
        task!.CurrentRunCount = 5;
        task.LastExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        await Storage.UpdateTask(task);

        // Simulate restart
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "state-change-restart-key");

        // Assert
        taskId2.ShouldBe(taskId);

        var afterRestartTasks = await Storage.Get(t => t.Id == taskId);
        var afterRestartTask = afterRestartTasks.FirstOrDefault();
        afterRestartTask.ShouldNotBeNull();
        afterRestartTask!.CurrentRunCount.ShouldBe(5);
        afterRestartTask.LastExecutionUtc.ShouldNotBeNull();
    }

    #endregion

    #region InProgress Task Handling

    [Fact]
    public async Task RecurringTask_WhenInProgress_ShouldReturnExistingId()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Create task that runs frequently (every second)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskConcurrent1(), // This task has a delay
            recurring => recurring.Schedule().Every(1).Seconds(),
            taskKey: "inprogress-key");

        // Wait for task to be in progress
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.InProgress);

        // Try to re-dispatch while InProgress
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskConcurrent1(),
            recurring => recurring.Schedule().Every(1).Seconds(),
            taskKey: "inprogress-key");

        // Assert - Should return same ID without modification
        taskId2.ShouldBe(taskId);

        // Wait for completion
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 10000);
    }

    #endregion

    #region SpecificRunTime with Different Intervals

    [Fact]
    public async Task SpecificRunTime_WithMinuteInterval_ShouldMaintainRhythm()
    {
        // Task starts at a specific time (30 minutes ago) and runs every 5 minutes
        var specificStart = DateTimeOffset.UtcNow.AddMinutes(-30);
        var recurringTask = new RecurringTask
        {
            SpecificRunTime = specificStart,
            MinuteInterval = new MinuteInterval(5)
        };

        var result = recurringTask.CalculateNextValidRun(specificStart, 0);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        result.SkippedCount.ShouldBeGreaterThan(0);

        // Verify rhythm from specific start time
        var minutesFromStart = (result.NextRun.Value - specificStart).TotalMinutes;
        var remainder = minutesFromStart % 5;

        (remainder < 0.1 || remainder > 4.9).ShouldBeTrue(
            $"NextRun should maintain 5-minute rhythm from {specificStart:HH:mm:ss}. " +
            $"Next run: {result.NextRun.Value:HH:mm:ss}, Minutes from start: {minutesFromStart:F2}");
    }

    [Fact]
    public async Task SpecificRunTime_InFuture_ShouldBeUsedAsFirstRun()
    {
        var futureStart = DateTimeOffset.UtcNow.AddHours(2);
        var recurringTask = new RecurringTask
        {
            SpecificRunTime = futureStart,
            HourInterval = new HourInterval(1)
        };

        var result = recurringTask.CalculateNextValidRun(DateTimeOffset.UtcNow, 0);

        result.NextRun.ShouldNotBeNull();
        // First run should be at the specific future time
        Math.Abs((result.NextRun!.Value - futureStart).TotalSeconds).ShouldBeLessThan(1);
        result.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task SpecificRunTime_WithHourInterval_AfterMissedRuns_ShouldMaintainRhythm()
    {
        // Task started 10 hours ago, runs every 2 hours
        var specificStart = DateTimeOffset.UtcNow.AddHours(-10);
        var recurringTask = new RecurringTask
        {
            SpecificRunTime = specificStart,
            HourInterval = new HourInterval(2)
        };

        // Simulate: last known scheduled time was 4 hours ago
        var lastScheduled = DateTimeOffset.UtcNow.AddHours(-4);
        var result = recurringTask.CalculateNextValidRun(lastScheduled, 3);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Should maintain 2-hour rhythm from specific start
        var hoursFromStart = (result.NextRun.Value - specificStart).TotalHours;
        var remainder = hoursFromStart % 2;

        (remainder < 0.1 || remainder > 1.9).ShouldBeTrue(
            $"NextRun should maintain 2-hour rhythm from {specificStart:HH:mm}. " +
            $"Next run: {result.NextRun.Value:HH:mm}, Hours from start: {hoursFromStart:F2}");
    }

    #endregion

    #region InitialDelay Handling

    [Fact]
    public async Task InitialDelay_ShouldBeIgnored_OnSubsequentRuns()
    {
        var recurringTask = new RecurringTask
        {
            InitialDelay = TimeSpan.FromMinutes(30),
            MinuteInterval = new MinuteInterval(5)
        };

        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(-10);

        // CurrentRun > 0 means InitialDelay should be ignored
        var result = recurringTask.CalculateNextValidRun(scheduledTime, 3);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Should use 5-minute interval, not 30-minute initial delay
        var minutesFromNow = (result.NextRun.Value - DateTimeOffset.UtcNow).TotalMinutes;
        minutesFromNow.ShouldBeLessThan(5.5);
    }

    [Fact]
    public async Task InitialDelay_ShouldBeUsed_OnFirstRun()
    {
        var recurringTask = new RecurringTask
        {
            InitialDelay = TimeSpan.FromMinutes(30),
            MinuteInterval = new MinuteInterval(5)
        };

        var now = DateTimeOffset.UtcNow;

        // CurrentRun == 0 means first run, InitialDelay should apply
        var result = recurringTask.CalculateNextRun(now, 0);

        result.ShouldNotBeNull();

        // Should be current time + 30 minutes
        var expectedTime = now.Add(TimeSpan.FromMinutes(30));
        Math.Abs((result!.Value - expectedTime).TotalSeconds).ShouldBeLessThan(1);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task VeryShortInterval_ShouldHandleManySkippedOccurrences()
    {
        // Task runs every second, started 5 minutes ago = 300 potential skips
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var recurringTask = new RecurringTask
        {
            SecondInterval = new SecondInterval(1)
        };

        // Use maxIterations to prevent excessive processing
        var result = recurringTask.CalculateNextValidRun(baseTime, 0, maxIterations: 500);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        result.SkippedCount.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task VeryLongInterval_ShouldNotSkip_WhenStillInFuture()
    {
        // Task runs monthly
        var baseTime = DateTimeOffset.UtcNow.AddDays(-10);
        var recurringTask = new RecurringTask
        {
            MonthInterval = new MonthInterval(1)
        };

        var result = recurringTask.CalculateNextValidRun(baseTime, 1);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        // Should not have skipped (next occurrence is still in the future)
        result.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ZeroCurrentRun_ShouldCalculateFirstRun()
    {
        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10)
        };

        var now = DateTimeOffset.UtcNow;
        var result = recurringTask.CalculateNextRun(now, 0);

        result.ShouldNotBeNull();
        // First run uses the interval (10 minutes)
        var diffMinutes = (result!.Value - now).TotalMinutes;
        // Should be approximately 10 minutes (with some tolerance for execution time)
        diffMinutes.ShouldBeGreaterThan(9);
        diffMinutes.ShouldBeLessThan(11);
    }

    [Fact]
    public async Task NullInterval_ShouldReturnNull()
    {
        var recurringTask = new RecurringTask
        {
            // No interval configured
        };

        var result = recurringTask.CalculateNextRun(DateTimeOffset.UtcNow, 1);

        result.ShouldBeNull();
    }

    #endregion

    #region Skipped Occurrences Tracking

    [Fact]
    public async Task SkippedOccurrences_ShouldBeInChronologicalOrder()
    {
        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5)
        };

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        var result = recurringTask.CalculateNextValidRun(baseTime, 1);

        result.SkippedCount.ShouldBeGreaterThan(1);
        result.SkippedOccurrences.Count.ShouldBe(result.SkippedCount);

        // Verify chronological order
        for (int i = 1; i < result.SkippedOccurrences.Count; i++)
        {
            result.SkippedOccurrences[i].ShouldBeGreaterThan(result.SkippedOccurrences[i - 1]);
        }
    }

    [Fact]
    public async Task SkippedOccurrences_ShouldAllBeInThePast()
    {
        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10)
        };

        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        var result = recurringTask.CalculateNextValidRun(baseTime, 1);

        result.SkippedCount.ShouldBeGreaterThan(0);

        // All skipped occurrences should be in the past
        foreach (var skipped in result.SkippedOccurrences)
        {
            skipped.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        }
    }

    #endregion

    #region Integration with TaskKey

    [Fact]
    public async Task RecurringTaskWithTaskKey_ChangingInterval_ShouldPreserveHistory()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Create task with 5-minute interval
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(5).Minutes(),
            taskKey: "interval-change-key");

        await Task.Delay(100);

        var initialTasks = await Storage.Get(t => t.Id == taskId);
        var initialTask = initialTasks.FirstOrDefault();
        initialTask.ShouldNotBeNull();
        initialTask!.RecurringInfo.ShouldNotBeNull();
        initialTask.RecurringInfo!.ShouldContain("5 minute");
        var originalCreatedAt = initialTask.CreatedAtUtc;

        // Update to 10-minute interval (simulating config change on restart)
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(10).Minutes(),
            taskKey: "interval-change-key");

        // Assert
        taskId2.ShouldBe(taskId);

        var updatedTasks = await Storage.Get(t => t.Id == taskId);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask.ShouldNotBeNull();
        updatedTask!.RecurringInfo.ShouldNotBeNull();
        updatedTask.RecurringInfo!.ShouldContain("10 minute");
        updatedTask.CreatedAtUtc.ShouldBe(originalCreatedAt); // History preserved
    }

    [Fact]
    public async Task RecurringTaskWithTaskKey_AddingMaxRuns_ShouldUpdate()
    {
        // Arrange
        await CreateIsolatedHostAsync();

        // Create unlimited recurring task
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours(),
            taskKey: "add-maxruns-key");

        await Task.Delay(100);

        // Update to add MaxRuns limit
        var taskId2 = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Hours().AtMinute(0).MaxRuns(100),
            taskKey: "add-maxruns-key");

        // Assert
        taskId2.ShouldBe(taskId);

        var updatedTasks = await Storage.Get(t => t.Id == taskId);
        var updatedTask = updatedTasks.FirstOrDefault();
        updatedTask.ShouldNotBeNull();
        updatedTask!.RecurringInfo.ShouldNotBeNull();
        // Verify MaxRuns was set (the RecurringTask should have MaxRuns = 100)
        var recurringTask = Newtonsoft.Json.JsonConvert.DeserializeObject<EverTask.Scheduler.Recurring.RecurringTask>(updatedTask.RecurringTask!);
        recurringTask!.MaxRuns.ShouldBe(100);
    }

    #endregion
}

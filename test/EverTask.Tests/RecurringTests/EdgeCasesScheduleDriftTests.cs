using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Tests for edge cases in recurring task scheduling with schedule drift fix.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class EdgeCasesScheduleDriftTests : IsolatedIntegrationTestBase
{
    #region MaxRuns Tests

    [Fact]
    public void MaxRuns_Should_Prevent_Rescheduling_When_Reached()
    {
        // Arrange: Task with MaxRuns = 3
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            MaxRuns = 3
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Try to calculate next run when already at max
        var nextRun = task.CalculateNextRun(scheduledTime, 3);

        // Assert: Should return null (no more runs allowed)
        Assert.Null(nextRun);
    }

    [Fact]
    public void MaxRuns_Should_Allow_Runs_Below_Limit()
    {
        // Arrange: Task with MaxRuns = 5
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            MaxRuns = 5
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run when below max (currentRun = 2)
        var nextRun = task.CalculateNextRun(scheduledTime, 2);

        // Assert: Should return next run
        Assert.NotNull(nextRun);
        Assert.Equal(scheduledTime.AddHours(1), nextRun);
    }

    [Fact]
    public async Task MaxRuns_Integration_Should_Stop_After_Limit()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with MaxRuns = 2, every 1 second
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(2));

        // Wait for both runs to complete
        await TaskWaitHelper.WaitForRecurringRunsAsync(Storage, taskId, expectedRuns: 2, timeoutMs: 5000);

        // Wait a bit longer to ensure no more runs happen
        await Task.Delay(2000);

        // Assert: Should have exactly 2 runs, no more
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.ShouldBe(2);

        // Task should not be scheduled anymore (reached MaxRuns)
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.NextRunUtc.ShouldBeNull();
    }

    #endregion

    #region RunUntil Tests

    [Fact]
    public void RunUntil_Should_Prevent_Rescheduling_After_Expiration()
    {
        // Arrange: Task with RunUntil in the past
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            RunUntil = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };

        // Scheduled time is after RunUntil
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should return null (past RunUntil)
        Assert.Null(nextRun);
    }

    [Fact]
    public void RunUntil_Should_Allow_Runs_Before_Expiration()
    {
        // Arrange: Task with RunUntil in the future
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            RunUntil = new DateTimeOffset(2024, 1, 1, 18, 0, 0, TimeSpan.Zero)
        };

        // Scheduled time is before RunUntil
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should return next run
        Assert.NotNull(nextRun);
        Assert.Equal(scheduledTime.AddHours(1), nextRun);
    }

    [Fact]
    public async Task RunUntil_Integration_Should_Stop_After_Expiration()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with RunUntil in 3 seconds, every 1 second
        var runUntil = DateTimeOffset.UtcNow.AddSeconds(3);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds().RunUntil(runUntil));

        // Wait for runs to complete (should stop at ~3 runs)
        await Task.Delay(5000);

        // Assert: Should have approximately 3 runs (may vary slightly due to timing)
        var counter = StateManager.GetCounter(nameof(TestTaskRecurringSeconds));
        counter.ShouldBeInRange(2, 4);

        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Task should be completed (reached RunUntil)
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.NextRunUtc.ShouldBeNull();
    }

    #endregion

    #region InitialDelay Tests

    [Fact]
    public void InitialDelay_Should_Add_Delay_To_First_Run()
    {
        // Arrange: Task with 30-minute initial delay
        var task = new RecurringTask
        {
            InitialDelay = TimeSpan.FromMinutes(30),
            HourInterval = new HourInterval(1)
        };

        var currentTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate first run (currentRun = 0)
        var nextRun = task.CalculateNextRun(currentTime, 0);

        // Assert: Should be current time + initial delay
        var expectedNextRun = currentTime.Add(TimeSpan.FromMinutes(30));
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void InitialDelay_Should_Be_Ignored_After_First_Run()
    {
        // Arrange: Task with initial delay
        var task = new RecurringTask
        {
            InitialDelay = TimeSpan.FromMinutes(30),
            HourInterval = new HourInterval(1)
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 30, 0, TimeSpan.Zero);

        // Act: Calculate second run (currentRun = 1)
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should use interval, not initial delay
        var expectedNextRun = scheduledTime.AddHours(1);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void InitialDelay_Plus_Interval_Should_Maintain_Gap()
    {
        // Arrange: Task with 30-second initial delay, then every 10 seconds
        var task = new RecurringTask
        {
            InitialDelay = TimeSpan.FromSeconds(30),
            SecondInterval = new SecondInterval(10)
        };

        var currentTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate first and second runs
        var firstRun = task.CalculateNextRun(currentTime, 0);
        var secondRun = task.CalculateNextRun(firstRun!.Value, 1);

        // Assert: First run = current + 30s, second run = first + 10s
        Assert.Equal(currentTime.AddSeconds(30), firstRun);
        Assert.Equal(firstRun.Value.AddSeconds(10), secondRun);
    }

    #endregion

    #region Long Downtime Handling Tests

    [Fact]
    public void CalculateNextValidRun_Should_Handle_Very_Old_Time_With_O1_Calculation()
    {
        // Arrange: Task runs every second
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(1)
        };

        // Very old scheduled time (1 year ago = ~31.5M seconds)
        var veryOldTime = DateTimeOffset.UtcNow.AddYears(-1);

        // Act: O(1) calculation should handle this instantly
        var result = task.CalculateNextValidRun(veryOldTime, 1);

        // Assert: With O(1) math, we should get a valid future run
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value > DateTimeOffset.UtcNow);

        // Should have calculated many skipped intervals
        Assert.True(result.SkippedCount > 1000);
    }

    [Fact]
    public void CalculateNextValidRun_Should_Handle_Very_Long_Downtime()
    {
        // Arrange: Task runs every hour
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Scheduled 10 years ago (extreme downtime)
        var extremelyOldTime = DateTimeOffset.UtcNow.AddYears(-10);

        // Act: O(1) calculation should handle this
        var result = task.CalculateNextValidRun(extremelyOldTime, 1);

        // Assert: Should calculate the next future run (not hit iteration limit)
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value > DateTimeOffset.UtcNow);

        // 10 years = ~87,600 hours
        Assert.True(result.SkippedCount > 80000);
    }

    [Fact]
    public void CalculateNextValidRun_With_Cron_Uses_O1_Via_Cronos()
    {
        // Arrange: Cron task (every hour at minute 0)
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 * * * *") // Every hour at :00
        };

        // Scheduled 5 hours ago
        var oldTime = DateTimeOffset.UtcNow.AddHours(-5);

        // Act: Cron uses Cronos.GetNextOccurrence which is O(1)
        var result = task.CalculateNextValidRun(oldTime, 1);

        // Assert: Should find a future run
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value > DateTimeOffset.UtcNow);
    }

    #endregion

    #region Timezone Tests

    [Fact]
    public void All_Calculations_Should_Use_UTC()
    {
        // Arrange: Task with hour interval
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Use UTC time
        var utcTime = DateTimeOffset.UtcNow;

        // Act
        var nextRun = task.CalculateNextRun(utcTime, 1);

        // Assert: Result should be in UTC
        Assert.NotNull(nextRun);
        Assert.Equal(TimeSpan.Zero, nextRun.Value.Offset);
    }

    [Fact]
    public void CalculateNextValidRun_Should_Use_UTC_For_Comparison()
    {
        // Arrange: Task runs every minute
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(1)
        };

        // Use UTC time in the past
        var pastUtcTime = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Act
        var result = task.CalculateNextValidRun(pastUtcTime, 1);

        // Assert: Result should be in UTC and in the future
        Assert.NotNull(result.NextRun);
        Assert.Equal(TimeSpan.Zero, result.NextRun.Value.Offset);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow);
    }

    #endregion

    #region Multiple Constraints Tests

    [Fact]
    public void MaxRuns_And_RunUntil_Should_Respect_First_Constraint_Hit()
    {
        // Arrange: Task with both MaxRuns and RunUntil
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            MaxRuns = 10,
            RunUntil = new DateTimeOffset(2024, 1, 1, 16, 0, 0, TimeSpan.Zero)
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate runs until one constraint is hit
        var runs = new List<DateTimeOffset?>();
        var currentRun = 0;
        var currentTime = scheduledTime;

        for (int i = 0; i < 15; i++)
        {
            var nextRun = task.CalculateNextRun(currentTime, currentRun);
            if (nextRun == null) break;

            runs.Add(nextRun);
            currentTime = nextRun.Value;
            currentRun++;
        }

        // Assert: Should stop before RunUntil (16:00), which allows only 1 run: 15:00
        // The second run would be 16:00, which equals RunUntil, so it should not be scheduled
        var singleRun = Assert.Single(runs);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 15, 0, 0, TimeSpan.Zero), singleRun);
    }

    [Fact]
    public void InitialDelay_With_RunUntil_Should_Work_Correctly()
    {
        // Arrange: Task with InitialDelay and RunUntil
        var task = new RecurringTask
        {
            InitialDelay = TimeSpan.FromHours(1),
            HourInterval = new HourInterval(1),
            RunUntil = new DateTimeOffset(2024, 1, 1, 17, 0, 0, TimeSpan.Zero)
        };

        var currentTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate first run
        var firstRun = task.CalculateNextRun(currentTime, 0);

        // Assert: First run = 15:00 (14:00 + 1 hour delay), which is before RunUntil
        Assert.NotNull(firstRun);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 15, 0, 0, TimeSpan.Zero), firstRun);

        // Act: Calculate second run
        var secondRun = task.CalculateNextRun(firstRun.Value, 1);

        // Assert: Second run = 16:00 (15:00 + 1 hour interval)
        Assert.NotNull(secondRun);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 16, 0, 0, TimeSpan.Zero), secondRun);

        // Act: Calculate third run
        var thirdRun = task.CalculateNextRun(secondRun.Value, 2);

        // Assert: Third run would be 17:00, which equals RunUntil, so should be null
        Assert.Null(thirdRun);
    }

    #endregion
}

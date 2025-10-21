using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Tests for the schedule drift fix in recurring tasks.
/// See: docs/recurring-task-schedule-drift-fix.md
/// </summary>
public class RecurringTaskScheduleDriftTests
{
    [Fact]
    public void CalculateNextRun_FromScheduledTime_ShouldMaintainSchedule()
    {
        // Arrange: Task configured to run every hour
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Simulate: Task was scheduled for 2:00 PM
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run from scheduled time (simulating WorkerExecutor behavior)
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Next run should be 3:00 PM (one hour after scheduled time, not current time)
        var expectedNextRun = new DateTimeOffset(2024, 1, 1, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void CalculateNextRun_WithMinuteInterval_ShouldCalculateFromScheduledTime()
    {
        // Arrange: Task runs every 15 minutes
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(15)
        };

        // Simulate: Task scheduled for 2:00 PM
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be 2:15 PM
        var expectedNextRun = new DateTimeOffset(2024, 1, 1, 14, 15, 0, TimeSpan.Zero);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void CalculateNextRun_WithSecondInterval_ShouldCalculateFromScheduledTime()
    {
        // Arrange: Task runs every 30 seconds
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(30)
        };

        // Simulate: Task scheduled for 2:00:00 PM
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be 2:00:30 PM
        var expectedNextRun = new DateTimeOffset(2024, 1, 1, 14, 0, 30, TimeSpan.Zero);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void CalculateNextRun_AfterDelay_ShouldNotDrift()
    {
        // Arrange: Task runs every hour at :00
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1) { OnMinute = 0 }
        };

        // Simulate: Task scheduled for 2:00 PM but executed at 2:45 PM
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate from scheduled time (not from when it actually ran)
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should still be 3:00 PM, not 3:45 PM
        var expectedNextRun = new DateTimeOffset(2024, 1, 1, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void CalculateNextRun_NextInPast_ReturnsTimeInPast()
    {
        // Arrange: Task runs every hour
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Simulate: Task scheduled for 2:00 PM (in the past)
        var scheduledTime = DateTimeOffset.UtcNow.AddHours(-3);

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Next run will be in the past (WorkerExecutor will skip it)
        Assert.NotNull(nextRun);
        Assert.True(nextRun.Value < DateTimeOffset.UtcNow);
    }

    [Fact]
    public void CalculateNextRun_WithCronExpression_ShouldCalculateFromScheduledTime()
    {
        // Arrange: Cron expression for every hour at :00 (0 * * * *)
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 * * * *")
        };

        // Simulate: Task scheduled for 2:00 PM
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be 3:00 PM
        var expectedNextRun = new DateTimeOffset(2024, 1, 1, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void CalculateNextRun_WithDayInterval_ShouldCalculateFromScheduledTime()
    {
        // Arrange: Task runs daily at 9:00 AM
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = new[] { new TimeOnly(9, 0, 0) } }
        };

        // Simulate: Task scheduled for Jan 1 at 9:00 AM
        var scheduledTime = new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be Jan 2 at 9:00 AM
        var expectedNextRun = new DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void CalculateNextRun_RespectsMaxRuns()
    {
        // Arrange: Task with MaxRuns = 5
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            MaxRuns = 5
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Try to calculate next run when already at max
        var nextRun = task.CalculateNextRun(scheduledTime, 5);

        // Assert: Should return null (reached max runs)
        Assert.Null(nextRun);
    }

    [Fact]
    public void CalculateNextRun_RespectsRunUntil()
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
    public void CalculateNextValidRun_SkipMultiplePastOccurrences()
    {
        // Arrange: Task runs every hour
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Simulate: System was down from 10:00 AM to 2:00 PM (missed 4 runs)
        var lastScheduledTime = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

        // Mock current time to be 2:30 PM
        // Note: The extension method uses DateTimeOffset.UtcNow internally,
        // so we test with a time that would be in the past
        var scheduledInPast = DateTimeOffset.UtcNow.AddHours(-5);

        // Act: Use the new extension method
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Should skip past occurrences and land on next valid time
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow, "Next run should be in the future");
        Assert.True(result.SkippedCount > 0, "Should have skipped some occurrences");
        Assert.True(result.SkippedCount < 10, "Should not have skipped too many (sanity check)");
        Assert.Equal(result.SkippedCount, result.SkippedOccurrences.Count);
    }

    [Fact]
    public void CalculateNextValidRun_NoSkipsWhenNextInFuture()
    {
        // Arrange: Task runs every hour
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Scheduled for near future
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(30);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1);

        // Assert: Should not skip anything
        Assert.NotNull(result.NextRun);
        Assert.Equal(0, result.SkippedCount);
        Assert.Empty(result.SkippedOccurrences);
    }

    [Fact]
    public void CalculateNextValidRun_InfiniteLoopProtection()
    {
        // Arrange: Task runs every hour
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Simulate: Very old scheduled time (10 years ago)
        var veryOldScheduledTime = DateTimeOffset.UtcNow.AddYears(-10);

        // Act: Should hit the safety limit
        var result = task.CalculateNextValidRun(veryOldScheduledTime, 1, maxIterations: 1000);

        // Assert: Should have stopped at max iterations
        Assert.Equal(1000, result.SkippedCount);
        Assert.Null(result.NextRun); // Should return null when limit exceeded
    }

    [Fact]
    public void CalculateNextValidRun_WithCustomMaxIterations()
    {
        // Arrange: Task runs every second
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(1)
        };

        var scheduledInPast = DateTimeOffset.UtcNow.AddMinutes(-2);

        // Act: Use custom max iterations
        var result = task.CalculateNextValidRun(scheduledInPast, 1, maxIterations: 50);

        // Assert: Should stop at 50 iterations
        Assert.True(result.SkippedCount <= 50);
    }

    [Fact]
    public void CalculateNextValidRun_SkippedOccurrencesAreInOrder()
    {
        // Arrange: Task runs every 10 minutes
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10)
        };

        var scheduledInPast = DateTimeOffset.UtcNow.AddHours(-1);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Skipped times should be in chronological order
        if (result.SkippedCount > 1)
        {
            for (int i = 1; i < result.SkippedOccurrences.Count; i++)
            {
                Assert.True(result.SkippedOccurrences[i] > result.SkippedOccurrences[i - 1],
                    "Skipped occurrences should be in chronological order");
            }
        }
    }

    [Fact]
    public void CalculateNextRun_WithMonthInterval_ShouldCalculateFromScheduledTime()
    {
        // Arrange: Task runs monthly on the 15th
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 15 }
        };

        // Simulate: Task scheduled for Jan 15
        var scheduledTime = new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be Feb 15
        Assert.NotNull(nextRun);
        Assert.Equal(2, nextRun.Value.Month);
        Assert.Equal(15, nextRun.Value.Day);
    }

    [Fact]
    public void CalculateNextRun_FirstRun_WithInitialDelay_ReturnsCorrectTime()
    {
        // Arrange: Task with initial delay
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
    public void CalculateNextRun_SubsequentRuns_IgnoresInitialDelay()
    {
        // Arrange: Task with initial delay
        var task = new RecurringTask
        {
            InitialDelay = TimeSpan.FromMinutes(30),
            HourInterval = new HourInterval(1)
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate second run (currentRun = 1)
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should use interval, not initial delay
        var expectedNextRun = scheduledTime.AddHours(1);
        Assert.Equal(expectedNextRun, nextRun);
    }
}

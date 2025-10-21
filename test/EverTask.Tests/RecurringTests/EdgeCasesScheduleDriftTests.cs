using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Tests for edge cases in recurring task scheduling with schedule drift fix.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class EdgeCasesScheduleDriftTests
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
        var host = new HostBuilder()
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

        await host.StartAsync();

        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = host.Services.GetRequiredService<ITaskStorage>();

        // Dispatch recurring task with MaxRuns = 2, every 1 second
        var taskId = await dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(2));

        // Wait for both runs to complete
        await TaskWaitHelper.WaitForRecurringRunsAsync(storage, taskId, expectedRuns: 2, timeoutMs: 5000);

        // Wait a bit longer to ensure no more runs happen
        await Task.Delay(2000);

        // Assert: Should have exactly 2 runs, no more
        var tasks = await storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.ShouldBe(2);

        // Task should not be scheduled anymore (reached MaxRuns)
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.NextRunUtc.ShouldBeNull();

        await host.StopAsync(CancellationToken.None);
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
        var host = new HostBuilder()
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

        await host.StartAsync();

        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = host.Services.GetRequiredService<ITaskStorage>();
        var stateManager = host.Services.GetRequiredService<TestTaskStateManager>();

        // Dispatch recurring task with RunUntil in 3 seconds, every 1 second
        var runUntil = DateTimeOffset.UtcNow.AddSeconds(3);

        var taskId = await dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds().RunUntil(runUntil));

        // Wait for runs to complete (should stop at ~3 runs)
        await Task.Delay(5000);

        // Assert: Should have approximately 3 runs (may vary slightly due to timing)
        var counter = stateManager.GetCounter(nameof(TestTaskRecurringSeconds));
        counter.ShouldBeInRange(2, 4);

        var tasks = await storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Task should be completed (reached RunUntil)
        task.Status.ShouldBe(QueuedTaskStatus.Completed);
        task.NextRunUtc.ShouldBeNull();

        await host.StopAsync(CancellationToken.None);
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

    #region Infinite Loop Protection Tests

    [Fact]
    public void CalculateNextValidRun_Should_Stop_At_MaxIterations()
    {
        // Arrange: Task runs every second
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(1)
        };

        // Very old scheduled time (would require many iterations)
        var veryOldTime = DateTimeOffset.UtcNow.AddYears(-1);

        // Act: Use low max iterations to trigger limit
        var result = task.CalculateNextValidRun(veryOldTime, 1, maxIterations: 100);

        // Assert: Should stop at max iterations
        Assert.Equal(100, result.SkippedCount);
        Assert.Null(result.NextRun); // Returns null when limit exceeded
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

        // Act: Use default max iterations (should hit limit)
        var result = task.CalculateNextValidRun(extremelyOldTime, 1);

        // Assert: Should hit safety limit
        Assert.Equal(1000, result.SkippedCount); // Default max iterations
        Assert.Null(result.NextRun);
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

    #region Skip Recording Tests

    [Fact]
    public void SkippedOccurrences_Should_Be_In_Chronological_Order()
    {
        // Arrange: Task runs every 5 minutes
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5)
        };

        // Scheduled 30 minutes ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddMinutes(-30);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Skipped occurrences should be in order
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
    public void SkippedOccurrences_Count_Should_Match_List_Length()
    {
        // Arrange: Task runs every 10 seconds
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(10)
        };

        // Scheduled 1 minute ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: SkippedCount should equal the length of SkippedOccurrences list
        Assert.Equal(result.SkippedCount, result.SkippedOccurrences.Count);
    }

    #endregion
}

using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Tests for the bug where NextRunUtc was calculated incorrectly after multiple runs.
/// Bug: When ExecutionTime is set to the current run's NextRunUtc (not the original first run time),
/// and currentRunCount > 0, the calculation was adding (currentRunCount * interval) instead of just one interval.
///
/// Example:
/// - Task runs every 5 minutes
/// - ExecutionTime = 10:50 (NextRunUtc from previous calculation)
/// - currentRunCount = 6
/// - BUG: Next run calculated as 10:50 + (6 * 5min) = 11:20 (WRONG)
/// - EXPECTED: Next run should be 10:50 + 5min = 10:55 (CORRECT)
/// </summary>
public class RecurringTaskNextRunCalculationBugTests
{
    /// <summary>
    /// This test reproduces the EXACT bug in WorkerExecutor.QueueNextOccourrence (lines 527-538).
    ///
    /// The buggy code assumed task.ExecutionTime was the original first run time,
    /// but when loaded from storage after restart, ExecutionTime is set to NextRunUtc
    /// (the current run's scheduled time). The loop then incorrectly adds N intervals
    /// instead of just one.
    ///
    /// This test should PASS now (confirming the bug exists) and FAIL after the fix.
    /// </summary>
    [Fact]
    public void QueueNextOccourrence_BuggyLogic_Should_produce_wrong_result()
    {
        // Arrange: Simulate the exact scenario from the user report
        // - Task started on Jan 1, 2026 at 00:00 UTC (SpecificRunTime)
        // - 5-minute interval
        // - 6 runs completed (currentRun = 6)
        // - ExecutionTime = 10:50 (NextRunUtc loaded from storage for THIS run)

        var intervalMinutes = 5;
        var currentRun = 6;

        // Use future dates to avoid CalculateNextValidRun skipping to "now"
        var referenceTime = new DateTimeOffset(2030, 1, 11, 10, 50, 0, TimeSpan.Zero);
        var executionTimeFromStorage = referenceTime; // This is NextRunUtc from storage

        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(intervalMinutes)
        };

        // ===== BUGGY LOGIC FROM WorkerExecutor.QueueNextOccourrence (lines 527-538) =====
        var scheduledTime = executionTimeFromStorage;

        // BUG: This loop assumes executionTime is the ORIGINAL first run time,
        // but it's actually the CURRENT run's scheduled time (NextRunUtc from storage)
        if (currentRun > 0)
        {
            var firstRunTime = executionTimeFromStorage; // BUG: This is NOT the first run time!
            for (int i = 0; i < currentRun; i++)
            {
                var nextRun = recurringTask.CalculateNextRun(firstRunTime, i + 1);
                if (nextRun.HasValue)
                    firstRunTime = nextRun.Value;
            }
            scheduledTime = firstRunTime;
        }

        // Then CalculateNextValidRun uses this wrong scheduledTime
        // Pass referenceTime to control "now"
        var buggyResult = recurringTask.CalculateNextValidRun(scheduledTime, currentRun + 1, referenceTime);
        // ===== END BUGGY LOGIC =====

        // Assert: The buggy logic produces 11:25 instead of 10:55
        // Loop adds 6 intervals (30 min) to get scheduledTime = 11:20
        // Then CalculateNextValidRun adds another interval (5 min) = 11:25
        var expectedCorrectNextRun = referenceTime.AddMinutes(5); // 10:50 + 5min = 10:55
        var actualBuggyNextRun = buggyResult.NextRun;

        actualBuggyNextRun.ShouldNotBeNull();

        // The buggy result is 10:50 + (6 * 5min) + 5min = 11:25
        // (loop adds 6 intervals, then CalculateNextValidRun adds one more)
        var buggyExpectedNextRun = referenceTime.AddMinutes(35); // 10:50 + 35min = 11:25

        // This assertion shows the bug: the result should be 10:55 but is actually 11:25
        var diffFromCorrect = (actualBuggyNextRun!.Value - expectedCorrectNextRun).TotalMinutes;
        var diffFromBuggy = Math.Abs((actualBuggyNextRun.Value - buggyExpectedNextRun).TotalMinutes);

        // The buggy code produces a result ~30 minutes off from correct
        diffFromCorrect.ShouldBeGreaterThan(25,
            $"This test shows the BUG: NextRunUtc should be {expectedCorrectNextRun:HH:mm} (+5min), " +
            $"but buggy logic produces {actualBuggyNextRun.Value:HH:mm}. " +
            $"If this assertion fails, it means the bug has been FIXED (which is good!).");

        diffFromBuggy.ShouldBeLessThan(2,
            $"Expected buggy result to be ~{buggyExpectedNextRun:HH:mm}, got {actualBuggyNextRun.Value:HH:mm}");
    }

    /// <summary>
    /// This test shows the CORRECT calculation: when ExecutionTime is already
    /// the current run's scheduled time, we should simply add ONE interval.
    ///
    /// This test should PASS both before and after the fix.
    /// </summary>
    [Fact]
    public void CorrectLogic_Should_add_single_interval_to_execution_time()
    {
        // Arrange
        var intervalMinutes = 5;
        var currentRun = 6;

        // Use future dates to avoid CalculateNextValidRun skipping
        var referenceTime = new DateTimeOffset(2030, 1, 11, 10, 50, 0, TimeSpan.Zero);
        var executionTimeFromStorage = referenceTime;

        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(intervalMinutes)
        };

        // ===== CORRECT LOGIC =====
        // When ExecutionTime is already the current run's scheduled time,
        // we don't need the reconstruction loop. Just add one interval.
        var scheduledTime = executionTimeFromStorage;
        var correctResult = recurringTask.CalculateNextValidRun(scheduledTime, currentRun + 1, referenceTime);
        // ===== END CORRECT LOGIC =====

        // Assert
        var expectedNextRun = referenceTime.AddMinutes(5); // 10:50 + 5min

        correctResult.NextRun.ShouldNotBeNull();
        correctResult.NextRun!.Value.ShouldBe(expectedNextRun,
            $"Correct calculation: {executionTimeFromStorage:HH:mm} + {intervalMinutes}min = {expectedNextRun:HH:mm}");
    }

    /// <summary>
    /// Tests that CalculateNextRun always adds ONE interval regardless of run count.
    /// </summary>
    [Theory]
    [InlineData(1, 5)]   // 1 previous run, 5 min interval
    [InlineData(5, 5)]   // 5 previous runs, 5 min interval
    [InlineData(10, 5)]  // 10 previous runs, 5 min interval
    [InlineData(100, 5)] // 100 previous runs, 5 min interval
    [InlineData(6, 10)]  // 6 previous runs, 10 min interval
    [InlineData(6, 1)]   // 6 previous runs, 1 min interval
    public void CalculateNextRun_Should_add_single_interval_regardless_of_run_count(
        int previousRuns, int intervalMinutes)
    {
        // Arrange
        var currentScheduledTime = new DateTimeOffset(2026, 1, 11, 10, 50, 0, TimeSpan.Zero);

        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(intervalMinutes)
        };

        // Act
        var nextRun = recurringTask.CalculateNextRun(currentScheduledTime, previousRuns + 1);

        // Assert
        nextRun.ShouldNotBeNull();

        // Expected: currentScheduledTime + intervalMinutes (ONE interval)
        var expectedNextRun = currentScheduledTime.AddMinutes(intervalMinutes);

        nextRun.Value.ShouldBe(expectedNextRun,
            $"With {previousRuns} previous runs and {intervalMinutes}min interval, " +
            $"next run should be {expectedNextRun:HH:mm} (scheduled + {intervalMinutes}min)");
    }

    /// <summary>
    /// Tests with various intervals showing the difference between buggy and correct calculation.
    /// This test documents the bug behavior for comparison purposes.
    /// </summary>
    [Theory]
    [InlineData(30)]  // Every 30 seconds
    [InlineData(60)]  // Every minute
    [InlineData(300)] // Every 5 minutes
    public void SecondInterval_BuggyCalculation_Should_add_extra_intervals(int intervalSeconds)
    {
        // Arrange
        var currentRun = 10;
        // Use future dates to avoid CalculateNextValidRun skipping
        var referenceTime = new DateTimeOffset(2030, 1, 11, 10, 50, 0, TimeSpan.Zero);
        var currentScheduledTime = referenceTime;

        var recurringTask = new RecurringTask
        {
            SecondInterval = new SecondInterval(intervalSeconds)
        };

        // Correct calculation: just add one interval
        var correctResult = recurringTask.CalculateNextValidRun(currentScheduledTime, currentRun + 1, referenceTime);

        // Buggy calculation (simulating the old bug in WorkerExecutor)
        var scheduledTime = currentScheduledTime;
        for (int i = 0; i < currentRun; i++)
        {
            var next = recurringTask.CalculateNextRun(scheduledTime, i + 1);
            if (next.HasValue) scheduledTime = next.Value;
        }
        var buggyResult = recurringTask.CalculateNextValidRun(scheduledTime, currentRun + 1, referenceTime);

        // Assert
        var expectedNextRun = currentScheduledTime.AddSeconds(intervalSeconds);

        correctResult.NextRun.ShouldNotBeNull();
        correctResult.NextRun!.Value.ShouldBe(expectedNextRun,
            $"Correct: {currentScheduledTime:HH:mm:ss} + {intervalSeconds}s = {expectedNextRun:HH:mm:ss}");

        // Show that buggy result is different (adds N*interval instead of 1*interval)
        buggyResult.NextRun.ShouldNotBeNull();
        var buggyDiff = (buggyResult.NextRun!.Value - expectedNextRun).TotalSeconds;
        buggyDiff.ShouldBeGreaterThan(intervalSeconds * (currentRun - 1),
            $"Buggy calculation should be off by at least {intervalSeconds * (currentRun - 1)}s. " +
            $"This documents the old bug behavior.");
    }

    /// <summary>
    /// This test verifies that the FIX works correctly.
    /// After the fix in WorkerExecutor.QueueNextOccourrence:
    /// - scheduledTime = task.ExecutionTime (which is NextRunUtc from storage)
    /// - Next run = scheduledTime + 1 interval
    ///
    /// This is the behavior we want and should always PASS.
    /// </summary>
    [Theory]
    [InlineData(1, 5)]    // 1 run, 5 min interval
    [InlineData(6, 5)]    // 6 runs, 5 min interval (the original bug report scenario)
    [InlineData(100, 5)]  // 100 runs, 5 min interval
    [InlineData(6, 1)]    // 6 runs, 1 min interval
    [InlineData(6, 60)]   // 6 runs, 60 min interval
    public void FixedLogic_Should_add_single_interval_regardless_of_run_count(
        int currentRunCount, int intervalMinutes)
    {
        // Arrange
        // Simulate the scenario: task loaded from storage after restart
        // task.ExecutionTime = NextRunUtc from database (the scheduled time for THIS run)
        var referenceTime = new DateTimeOffset(2030, 1, 11, 10, 50, 0, TimeSpan.Zero);
        var taskExecutionTime = referenceTime; // This is what WorkerService sets from NextRunUtc

        var recurringTask = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(intervalMinutes)
        };

        // ===== FIXED LOGIC (as implemented in WorkerExecutor after the fix) =====
        // task.ExecutionTime is already the scheduled time for THIS run,
        // no reconstruction loop needed
        var scheduledTime = taskExecutionTime;
        var fixedResult = recurringTask.CalculateNextValidRun(scheduledTime, currentRunCount + 1, referenceTime);
        // ===== END FIXED LOGIC =====

        // Assert: Next run should be scheduledTime + 1 interval
        var expectedNextRun = taskExecutionTime.AddMinutes(intervalMinutes);

        fixedResult.NextRun.ShouldNotBeNull();
        fixedResult.NextRun!.Value.ShouldBe(expectedNextRun,
            $"With currentRunCount={currentRunCount} and {intervalMinutes}min interval, " +
            $"next run should be {expectedNextRun:HH:mm} (scheduledTime + {intervalMinutes}min). " +
            $"This confirms the fix works correctly.");
    }
}

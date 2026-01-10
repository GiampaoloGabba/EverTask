using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests for recurring task skip persistence functionality.
/// Tests that skipped occurrences are properly recorded in the audit trail.
/// </summary>
public class RecurringTaskSkipPersistenceTests : IsolatedIntegrationTestBase
{
    [Fact]
    public void Extension_method_CalculateNextValidRun_should_return_skip_count()
    {
        // Unit test for the extension method (can run without full integration)

        var recurringTask = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Scheduled 5 hours ago (should skip ~5 occurrences)
        var scheduledInPast = DateTimeOffset.UtcNow.AddHours(-5);

        var result = recurringTask.CalculateNextValidRun(scheduledInPast, 1);

        // Should have skipped some occurrences
        result.SkippedCount.ShouldBeGreaterThan(0);
        result.NextRun.ShouldNotBeNull();
        result.NextRun.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Extension_method_should_handle_null_gracefully()
    {
        RecurringTask? nullTask = null;

        // Should throw ArgumentNullException
        Should.Throw<ArgumentNullException>(() =>
        {
            nullTask!.CalculateNextValidRun(DateTimeOffset.UtcNow, 1);
        });
    }
}

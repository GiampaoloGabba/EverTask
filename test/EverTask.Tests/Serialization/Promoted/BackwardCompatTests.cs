using Newtonsoft.Json;

using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// The most important guarantee: data already persisted in production DBs (written by Newtonsoft) must
/// still deserialize under the new STJ serializer. Newtonsoft here is the "legacy producer"; STJ is the
/// reader. The Newtonsoft settings mirror the production <c>EverTaskJson</c> (TypeNameHandling.None).
/// </summary>
public class BackwardCompatTests
{
    private static readonly JsonSerializerSettings LegacySettings = new()
    {
        TypeNameHandling = TypeNameHandling.None
    };

    private static readonly DateTimeOffset T0 = new(2026, 6, 17, 10, 0, 0, TimeSpan.Zero);

    private static string Legacy(object value) => JsonConvert.SerializeObject(value, LegacySettings);

    [Fact]
    public void STJ_reads_legacy_cron_recurring_task()
    {
        var original   = new RecurringTask { CronInterval = new CronInterval("*/15 * * * *"), MaxRuns = 10 };
        var legacyJson = Legacy(original);

        var restored = EverTaskJson.Deserialize<RecurringTask>(legacyJson)!;

        restored.CronInterval!.CronExpression.ShouldBe("*/15 * * * *");
        restored.MaxRuns.ShouldBe(10);
        restored.CalculateNextRun(T0, 1).ShouldBe(original.CalculateNextRun(T0, 1));
    }

    [Fact]
    public void STJ_reads_legacy_timespan_and_datetimeoffset()
    {
        var original = new RecurringTask
        {
            InitialDelay    = TimeSpan.FromMinutes(90).Add(TimeSpan.FromSeconds(30)),
            SpecificRunTime = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            RunUntil        = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            MinuteInterval  = new MinuteInterval(10)
        };

        var restored = EverTaskJson.Deserialize<RecurringTask>(Legacy(original))!;

        // Proves TimeSpan ("c" format) and DateTimeOffset (ISO 8601) written by Newtonsoft parse under STJ.
        restored.InitialDelay.ShouldBe(original.InitialDelay);
        restored.SpecificRunTime.ShouldBe(original.SpecificRunTime);
        restored.RunUntil.ShouldBe(original.RunUntil);
    }

    [Fact]
    public void STJ_reads_legacy_month_interval_with_timeonly()
    {
        var original = new RecurringTask
        {
            MonthInterval = new MonthInterval(1, new[] { 3, 9 })
            {
                OnDay   = 10,
                OnTimes = new[] { new TimeOnly(7, 45), new TimeOnly(19, 15) }
            }
        };

        var restored = EverTaskJson.Deserialize<RecurringTask>(Legacy(original))!;

        restored.MonthInterval!.OnDay.ShouldBe(10);
        restored.MonthInterval!.OnMonths.ShouldBe(new[] { 3, 9 });
        // Proves TimeOnly[] written by Newtonsoft parses under STJ.
        restored.MonthInterval!.OnTimes.ShouldBe(new[] { new TimeOnly(7, 45), new TimeOnly(19, 15) });
    }

    [Fact]
    public void STJ_reads_legacy_complex_payload()
    {
        var original = new ComplexTask(
            Guid.NewGuid(), 7, 5_000_000_000L, 99.99m, 0.5, false,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)),
            TimeSpan.FromSeconds(123), PocPriority.Normal, "hello",
            new List<int> { 9, 8 }, new[] { "x" },
            new Dictionary<string, string> { ["a"] = "1" },
            new NestedDto("n", 3) { Flag = true });

        var restored = EverTaskJson.Deserialize<ComplexTask>(Legacy(original))!;

        restored.BigNumber.ShouldBe(5_000_000_000L);
        restored.Amount.ShouldBe(99.99m);
        restored.When.ShouldBe(original.When);
        restored.Ttl.ShouldBe(original.Ttl);
        restored.Priority.ShouldBe(PocPriority.Normal);
        restored.Metadata.ShouldBe(original.Metadata);
        restored.Nested.Flag.ShouldBeTrue();
    }

    [Fact]
    public void Legacy_DayInterval_OnDays_is_preserved_after_public_setter_fix()
    {
        // PRE-fix this asserted OnDays was LOST (internal setter dropped by STJ). B2 made OnDays a public
        // setter on the production DayInterval/WeekInterval, so a legacy Newtonsoft row now round-trips with
        // its OnDays schedule intact under STJ — no backward-compat data loss.
        var original   = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Saturday });
        var legacyJson = Legacy(original);
        legacyJson.ShouldContain("OnDays");

        var restored = EverTaskJson.Deserialize<DayInterval>(legacyJson)!;

        restored.OnDays.ShouldBe(new[] { DayOfWeek.Monday, DayOfWeek.Saturday });
    }
}

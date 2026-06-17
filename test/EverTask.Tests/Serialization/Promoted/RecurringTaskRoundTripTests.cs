using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Round-trip of EverTask's OWN persisted types (RecurringTask + intervals) through the candidate STJ
/// serializer. A fixed reference instant is used everywhere (no wall clock) so assertions are deterministic.
/// </summary>
public class RecurringTaskRoundTripTests
{
    // Fixed anchor: 2026-06-17 10:00:00 UTC.
    private static readonly DateTimeOffset T0 = new(2026, 6, 17, 10, 0, 0, TimeSpan.Zero);

    private static RecurringTask RoundTrip(RecurringTask original)
    {
        var json = EverTaskJson.Serialize(original);
        return EverTaskJson.Deserialize<RecurringTask>(json)!;
    }

    private static void AssertSameSchedule(RecurringTask original, RecurringTask restored)
    {
        // The next 5 occurrences from T0 must be identical.
        var a = original;
        var b = restored;
        for (var run = 0; run < 5; run++)
        {
            var oa = a.CalculateNextRun(T0, run);
            var ob = b.CalculateNextRun(T0, run);
            ob.ShouldBe(oa, $"divergence at occurrence #{run}");
        }
        restored.ToString().ShouldBe(original.ToString());
    }

    [Fact]
    public void Cron_roundtrips()
    {
        var original = new RecurringTask { CronInterval = new CronInterval("0 0 * * *") };
        var restored = RoundTrip(original);
        restored.CronInterval.ShouldNotBeNull();
        restored.CronInterval!.CronExpression.ShouldBe("0 0 * * *");
        AssertSameSchedule(original, restored);
    }

    [Fact]
    public void Second_minute_hour_cadence_roundtrips()
    {
        var original = new RecurringTask
        {
            HourInterval   = new HourInterval(2) { OnMinute = 15, OnSecond = 30 },
            MinuteInterval = new MinuteInterval(5) { OnSecond = 10 },
            SecondInterval = new SecondInterval(45)
        };
        var restored = RoundTrip(original);
        restored.HourInterval!.Interval.ShouldBe(2);
        restored.HourInterval!.OnMinute.ShouldBe(15);
        restored.HourInterval!.OnSecond.ShouldBe(30);
        restored.MinuteInterval!.Interval.ShouldBe(5);
        restored.MinuteInterval!.OnSecond.ShouldBe(10);
        restored.SecondInterval!.Interval.ShouldBe(45);
        AssertSameSchedule(original, restored);
    }

    [Fact]
    public void Day_cadence_without_OnDays_roundtrips()
    {
        // Pure cadence (Interval > 0, no OnDays) avoids the internal-setter gap.
        var original = new RecurringTask { DayInterval = new DayInterval(3) };
        var restored = RoundTrip(original);
        restored.DayInterval!.Interval.ShouldBe(3);
        AssertSameSchedule(original, restored);
    }

    [Fact]
    public void Month_with_OnDay_OnMonths_OnTimes_roundtrips()
    {
        // MonthInterval.OnDays/OnMonths are PUBLIC setters (F11 fix) → must round-trip fully.
        var original = new RecurringTask
        {
            MonthInterval = new MonthInterval(1, new[] { 1, 6, 12 })
            {
                OnDay   = 15,
                OnTimes = new[] { new TimeOnly(8, 30), new TimeOnly(20, 0) }
            }
        };
        var restored = RoundTrip(original);
        restored.MonthInterval.ShouldNotBeNull();
        restored.MonthInterval!.Interval.ShouldBe(1);
        restored.MonthInterval!.OnDay.ShouldBe(15);
        restored.MonthInterval!.OnMonths.ShouldBe(new[] { 1, 6, 12 });
        restored.MonthInterval!.OnTimes.ShouldBe(new[] { new TimeOnly(8, 30), new TimeOnly(20, 0) });
        AssertSameSchedule(original, restored);
    }

    [Fact]
    public void InitialDelay_TimeSpan_roundtrips()
    {
        var original = new RecurringTask
        {
            InitialDelay   = TimeSpan.FromMinutes(90).Add(TimeSpan.FromSeconds(30)),
            MinuteInterval = new MinuteInterval(10)
        };
        var restored = RoundTrip(original);
        restored.InitialDelay.ShouldBe(TimeSpan.FromMinutes(90).Add(TimeSpan.FromSeconds(30)));
        AssertSameSchedule(original, restored);
    }

    [Fact]
    public void RunUntil_SpecificRunTime_DateTimeOffset_MaxRuns_roundtrip()
    {
        var original = new RecurringTask
        {
            SpecificRunTime = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            RunUntil        = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            MaxRuns         = 7,
            DayInterval     = new DayInterval(1)
        };
        var restored = RoundTrip(original);
        restored.SpecificRunTime.ShouldBe(original.SpecificRunTime);
        restored.RunUntil.ShouldBe(original.RunUntil);
        restored.MaxRuns.ShouldBe(7);
        AssertSameSchedule(original, restored);
    }
}

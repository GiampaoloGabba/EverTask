using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Per-interval coverage: every serialized field of every <c>IInterval</c> implementation must survive a
/// STJ round-trip AND a legacy Newtonsoft→STJ read. These are the building blocks of <c>RecurringTask</c>,
/// so "STJ can (de)serialize everything we need" starts here.
/// </summary>
public class IntervalSerializationTests
{
    private static T Rt<T>(T value) => EverTaskJson.Deserialize<T>(EverTaskJson.Serialize(value))!;

    private static readonly Newtonsoft.Json.JsonSerializerSettings Legacy =
        new() { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None };

    private static T FromLegacy<T>(T value) =>
        EverTaskJson.Deserialize<T>(Newtonsoft.Json.JsonConvert.SerializeObject(value, Legacy))!;

    [Fact]
    public void SecondInterval_roundtrips()
    {
        var r = Rt(new SecondInterval(45));
        r.Interval.ShouldBe(45);
        FromLegacy(new SecondInterval(45)).Interval.ShouldBe(45);
    }

    [Fact]
    public void MinuteInterval_roundtrips_all_fields()
    {
        var r = Rt(new MinuteInterval(5) { OnSecond = 20 });
        r.Interval.ShouldBe(5);
        r.OnSecond.ShouldBe(20);

        var l = FromLegacy(new MinuteInterval(5) { OnSecond = 20 });
        l.OnSecond.ShouldBe(20);
    }

    [Fact]
    public void HourInterval_roundtrips_all_fields_including_OnHours()
    {
        var original = new HourInterval(2, new[] { 8, 13, 22 }) { OnMinute = 30, OnSecond = 15 };
        var r = Rt(original);
        r.Interval.ShouldBe(2);
        r.OnMinute.ShouldBe(30);
        r.OnSecond.ShouldBe(15);
        r.OnHours.ShouldBe(new[] { 8, 13, 22 }); // public setter → survives

        var l = FromLegacy(original);
        l.OnHours.ShouldBe(new[] { 8, 13, 22 });
        l.OnMinute.ShouldBe(30);
    }

    [Fact]
    public void MonthInterval_roundtrips_all_fields()
    {
        var original = new MonthInterval(1, new[] { 1, 6, 12 })
        {
            OnDay   = 15,
            OnFirst = DayOfWeek.Monday,
            OnDays  = new[] { 3, 17 },                                  // public setter (F11) → survives
            OnTimes = new[] { new TimeOnly(8, 30), new TimeOnly(20, 0) }
        };
        var r = Rt(original);
        r.Interval.ShouldBe(1);
        r.OnDay.ShouldBe(15);
        r.OnFirst.ShouldBe(DayOfWeek.Monday);
        r.OnDays.ShouldBe(new[] { 3, 17 });
        r.OnMonths.ShouldBe(new[] { 1, 6, 12 });
        r.OnTimes.ShouldBe(new[] { new TimeOnly(8, 30), new TimeOnly(20, 0) });

        var l = FromLegacy(original);
        l.OnMonths.ShouldBe(new[] { 1, 6, 12 });
        l.OnFirst.ShouldBe(DayOfWeek.Monday);
        l.OnTimes.ShouldBe(new[] { new TimeOnly(8, 30), new TimeOnly(20, 0) });
    }

    [Fact]
    public void DayInterval_cadence_and_OnTimes_roundtrip()
    {
        var original = new DayInterval(3) { OnTimes = new[] { new TimeOnly(6, 0), new TimeOnly(18, 0) } };
        var r = Rt(original);
        r.Interval.ShouldBe(3);
        r.OnTimes.ShouldBe(new[] { new TimeOnly(6, 0), new TimeOnly(18, 0) });
        // OnDays (internal setter) gap is pinned by InternalSetterRegressionTests.
    }

    [Fact]
    public void WeekInterval_cadence_and_OnTimes_roundtrip()
    {
        var original = new WeekInterval(2) { OnTimes = new[] { new TimeOnly(9, 15) } };
        var r = Rt(original);
        r.Interval.ShouldBe(2);
        r.OnTimes.ShouldBe(new[] { new TimeOnly(9, 15) });
    }

    [Fact]
    public void CronInterval_roundtrips_and_parses()
    {
        var r = Rt(new CronInterval("0 0 12 * * *"));
        r.CronExpression.ShouldBe("0 0 12 * * *");
        r.ParseCronExpression().ShouldNotBeNull(); // private cache field is recomputed, not serialized

        FromLegacy(new CronInterval("*/15 * * * *")).CronExpression.ShouldBe("*/15 * * * *");
    }

    [Fact] // B2 landed the OnDays public-setter fix on the production DayInterval/WeekInterval → un-skipped.
    public void DayInterval_OnDays_should_roundtrip_after_fix()
    {
        var r = Rt(new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Friday }));
        r.OnDays.ShouldBe(new[] { DayOfWeek.Monday, DayOfWeek.Friday });
    }
}

using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Intervals;

/// <summary>
/// B2: the per-interval <c>Validate()</c> contract that recovery invokes (via <see cref="RecurringTask.Validate"/>)
/// right after deserializing a persisted schedule. Corrupt-but-deserializable metadata (unparseable cron,
/// out-of-range OnDays/OnHours/OnMonths, negative Interval) must throw here so the recovery routes it to the
/// terminal poison path (B1) — instead of throwing downstream at next-run or producing a wrong schedule. These
/// are pure invariants on the REAL interval/RecurringTask objects (no host needed), so they are unit tests.
/// </summary>
public class IntervalValidationTests
{
    // --- valid schedules must NOT throw ---

    [Fact]
    public void Valid_intervals_do_not_throw()
    {
        Should.NotThrow(() => new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Friday }).Validate());
        Should.NotThrow(() => new DayInterval(2).Validate());
        Should.NotThrow(() => new WeekInterval(0, new[] { DayOfWeek.Sunday }).Validate());
        Should.NotThrow(() => new WeekInterval(1).Validate());
        Should.NotThrow(() => new MonthInterval(0, new[] { 1, 6, 12 }).Validate());
        Should.NotThrow(() => new MonthInterval(1) { OnDay = 15, OnDays = new[] { 1, 28 } }.Validate());
        Should.NotThrow(() => new HourInterval(0, new[] { 0, 9, 23 }) { OnMinute = 30, OnSecond = 59 }.Validate());
        Should.NotThrow(() => new HourInterval(3).Validate());
        Should.NotThrow(() => new MinuteInterval(5) { OnSecond = 30 }.Validate());
        Should.NotThrow(() => new SecondInterval(10).Validate());
        Should.NotThrow(() => new CronInterval("*/10 * * * * *").Validate());
        Should.NotThrow(() => new CronInterval("0 9 * * 1-5").Validate());
        Should.NotThrow(() => new CronInterval().Validate()); // empty cron = not used, not corrupt
    }

    // --- DayOfWeek out of range ---

    [Fact]
    public void DayInterval_with_out_of_range_OnDays_throws() =>
        Should.Throw<ArgumentException>(() => new DayInterval(0, new[] { (DayOfWeek)99 }).Validate());

    [Fact]
    public void WeekInterval_with_out_of_range_OnDays_throws() =>
        Should.Throw<ArgumentException>(() => new WeekInterval(0, new[] { (DayOfWeek)42 }).Validate());

    // --- negative interval ---

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Negative_interval_throws(int interval)
    {
        Should.Throw<ArgumentException>(() => new SecondInterval(interval).Validate());
        Should.Throw<ArgumentException>(() => new MinuteInterval(interval).Validate());
        Should.Throw<ArgumentException>(() => new HourInterval(interval).Validate());
        Should.Throw<ArgumentException>(() => new DayInterval(interval).Validate());
        Should.Throw<ArgumentException>(() => new WeekInterval(interval).Validate());
        Should.Throw<ArgumentException>(() => new MonthInterval(interval).Validate());
    }

    // --- out-of-range selector arrays / fields ---

    [Fact]
    public void HourInterval_with_out_of_range_OnHours_throws() =>
        Should.Throw<ArgumentException>(() => new HourInterval(0, new[] { 99 }).Validate());

    [Fact]
    public void HourInterval_with_out_of_range_OnMinute_or_OnSecond_throws()
    {
        Should.Throw<ArgumentException>(() => new HourInterval(1) { OnMinute = 99 }.Validate());
        Should.Throw<ArgumentException>(() => new HourInterval(1) { OnSecond = -5 }.Validate());
    }

    [Fact]
    public void MinuteInterval_with_out_of_range_OnSecond_throws() =>
        Should.Throw<ArgumentException>(() => new MinuteInterval(1) { OnSecond = 99 }.Validate());

    [Fact]
    public void MonthInterval_with_out_of_range_OnMonths_throws() =>
        Should.Throw<ArgumentException>(() => new MonthInterval(0, new[] { 13 }).Validate());

    [Fact]
    public void MonthInterval_with_out_of_range_OnDay_or_OnDays_throws()
    {
        Should.Throw<ArgumentException>(() => new MonthInterval(1) { OnDay = 40 }.Validate());
        Should.Throw<ArgumentException>(() => new MonthInterval(1) { OnDays = new[] { 99 } }.Validate());
    }

    // --- cron parseability ---

    [Theory]
    [InlineData("this is not a cron")]
    [InlineData("not-a-cron")]
    [InlineData("99 99 99 99 99")]
    public void CronInterval_with_unparseable_expression_throws(string cron) =>
        Should.Throw<Exception>(() => new CronInterval(cron).Validate());

    // --- RecurringTask.Validate delegates to every present interval ---

    [Fact]
    public void RecurringTask_Validate_throws_when_any_present_interval_is_corrupt()
    {
        Should.Throw<ArgumentException>(() =>
            new RecurringTask { DayInterval = new DayInterval(0, new[] { (DayOfWeek)99 }) }.Validate());

        Should.Throw<Exception>(() =>
            new RecurringTask { CronInterval = new CronInterval("not a cron") }.Validate());

        Should.Throw<ArgumentException>(() =>
            new RecurringTask { HourInterval = new HourInterval(0, new[] { 99 }) }.Validate());
    }

    [Fact]
    public void RecurringTask_Validate_does_not_throw_for_a_valid_schedule() =>
        Should.NotThrow(() => new RecurringTask
        {
            DayInterval = new DayInterval(0, new[] { DayOfWeek.Monday }) { OnTimes = new[] { new TimeOnly(9, 0) } },
            MaxRuns     = 5
        }.Validate());
}

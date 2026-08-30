using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class BuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public BuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_EveryMinute_AtSpecificSecond()
    {
        _builder.Schedule().EveryMinute().AtSecond(30);

        Assert.NotNull(_builder.RecurringTask.MinuteInterval);
        Assert.Equal(30, _builder.RecurringTask.MinuteInterval.OnSecond);
    }

    [Fact]
    public void Should_Set_EveryHour_AtSpecificMinute_And_MaxRuns()
    {
        _builder.Schedule().EveryHour().AtMinute(45).MaxRuns(10);

        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(45, _builder.RecurringTask.HourInterval.OnMinute);
        Assert.Equal(10, _builder.RecurringTask.MaxRuns);
        Assert.Null(_builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryHour_AtSpecificMinute_And_RunUntil_UTC()
    {
        var runUntil = DateTimeOffset.Now.AddMinutes(2);
        _builder.Schedule().EveryHour().AtMinute(45).RunUntil(runUntil);

        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(45, _builder.RecurringTask.HourInterval.OnMinute);
        Assert.Null(_builder.RecurringTask.MaxRuns);
        Assert.Equal(runUntil.ToUniversalTime(), _builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryDay_AtMultipleTimes()
    {
        var times = new[] { new TimeOnly(9, 0), new TimeOnly(15, 0), new TimeOnly(21, 0) };
        _builder.Schedule().EveryDay().AtTimes(times);

        Assert.NotNull(_builder.RecurringTask.DayInterval);
        Assert.Equal(times, _builder.RecurringTask.DayInterval.OnTimes);
    }

    [Fact]
    public void Should_Set_RunNow_EveryMonth_OnSpecificDay()
    {
        _builder.RunNow().Then().EveryMonth().OnDay(15);

        Assert.True(_builder.RecurringTask.RunNow);
        Assert.NotNull(_builder.RecurringTask.MonthInterval);
        Assert.Equal(15, _builder.RecurringTask.MonthInterval.OnDay);
    }

    [Fact]
    public void Should_union_each_exclusion_kind_across_repeated_calls()
    {
        var from = new DateTimeOffset(2026, 12, 24, 22, 0, 0, TimeSpan.FromHours(1));
        var to = from.AddHours(4);

        _builder.Schedule().EveryDay()
            .Except(exclusions => exclusions
                .OnDays(DayOfWeek.Monday)
                .OnDates(new DateOnly(2026, 12, 25))
                .Between(from, to))
            .Except(exclusions => exclusions
                .OnDays(DayOfWeek.Friday, DayOfWeek.Monday)
                .OnDates(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25)));

        _builder.RecurringTask.Validate();

        var exclusions = _builder.RecurringTask.Exclusions.ShouldNotBeNull();
        exclusions.Days.ShouldBe([DayOfWeek.Monday, DayOfWeek.Friday]);
        exclusions.Dates.ShouldBe([new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25)]);
        exclusions.Ranges.ShouldHaveSingleItem().FromUtc.ShouldBe(from.ToUniversalTime());
        exclusions.Ranges.ShouldHaveSingleItem().ToUtc.ShouldBe(to.ToUniversalTime());
    }

    [Fact]
    public void Should_make_ExceptWeekends_equivalent_to_the_two_days()
    {
        var sugar = new RecurringTaskBuilder();
        var explicitDays = new RecurringTaskBuilder();

        sugar.Schedule().EveryDay().ExceptWeekends();
        explicitDays.Schedule().EveryDay()
            .Except(exclusions => exclusions.OnDays(DayOfWeek.Saturday, DayOfWeek.Sunday));
        sugar.RecurringTask.Validate();
        explicitDays.RecurringTask.Validate();

        sugar.RecurringTask.Exclusions.ShouldNotBeNull().Days
            .ShouldBe(explicitDays.RecurringTask.Exclusions.ShouldNotBeNull().Days);
    }

    [Fact]
    public void Should_refuse_an_empty_Except_callback()
    {
        Should.Throw<InvalidOperationException>(() => _builder.Schedule().EveryDay().Except(_ => { }));
    }

    [Fact]
    public void Should_refuse_a_non_positive_exclusion_window_immediately()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Should.Throw<ArgumentException>(() =>
            _builder.Schedule().EveryDay().Except(exclusions => exclusions.Between(instant, instant)));
    }

    public static TheoryData<string, Action<IRecurringTaskBuilder>> RefiningBuilders => new()
    {
        { "interval", recurring => recurring.Schedule().ExceptWeekends().EveryDay() },
        { "hour", recurring => recurring.Schedule().EveryHour().ExceptWeekends().AtMinute(30) },
        { "minute", recurring => recurring.Schedule().EveryMinute().ExceptWeekends().AtSecond(30) },
        { "daily", recurring => recurring.Schedule().EveryDay().ExceptWeekends().AtTime(new TimeOnly(9, 0)) },
        { "weekly", recurring => recurring.Schedule().EveryWeek().ExceptWeekends().OnDay(DayOfWeek.Monday) },
        { "monthly", recurring => recurring.Schedule().EveryMonth().ExceptWeekends().OnDay(15) }
    };

    [Theory]
    [MemberData(nameof(RefiningBuilders))]
    public void Should_preserve_each_refining_builder_after_an_exclusion(
        string shape, Action<IRecurringTaskBuilder> configure)
    {
        configure(_builder);

        _builder.RecurringTask.Exclusions.ShouldNotBeNull(
            $"the {shape} builder must apply the exclusion without ending its refinement chain");
    }

    [Fact]
    public void Should_accept_calendar_exclusions_on_both_sides_of_InTimeZone()
    {
        var zoneThenExclusion = new RecurringTaskBuilder();
        var exclusionThenZone = new RecurringTaskBuilder();

        zoneThenExclusion.Schedule().Every(4).Hours().InTimeZone("Europe/Rome").ExceptWeekends();
        exclusionThenZone.Schedule().Every(4).Hours().ExceptWeekends().InTimeZone("Europe/Rome");

        Should.NotThrow(() => zoneThenExclusion.RecurringTask.Validate());
        Should.NotThrow(() => exclusionThenZone.RecurringTask.Validate());
        zoneThenExclusion.RecurringTask.Exclusions.ShouldNotBeNull().Days
            .ShouldBe(exclusionThenZone.RecurringTask.Exclusions.ShouldNotBeNull().Days);
    }
}

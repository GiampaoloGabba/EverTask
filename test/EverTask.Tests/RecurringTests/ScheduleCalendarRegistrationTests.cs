using EverTask.Scheduler.Recurring;

namespace EverTask.Tests.RecurringTests;

public class ScheduleCalendarRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_refuse_a_blank_calendar_name(string? name)
    {
        var options = new EverTaskServiceConfiguration();

        Should.Throw<ArgumentException>(() =>
            options.AddScheduleCalendar(name!, calendar => calendar.OnDates(new DateOnly(2026, 1, 1))));
    }

    [Fact]
    public void Should_refuse_a_calendar_name_longer_than_one_hundred_trimmed_characters()
    {
        var options = new EverTaskServiceConfiguration();

        Should.Throw<ArgumentException>(() =>
            options.AddScheduleCalendar($" {new string('x', 101)} ",
                calendar => calendar.OnDates(new DateOnly(2026, 1, 1))));
    }

    [Fact]
    public void Should_refuse_a_duplicate_trimmed_calendar_name()
    {
        var options = new EverTaskServiceConfiguration()
            .AddScheduleCalendar("holidays", calendar => calendar.OnDates(new DateOnly(2026, 1, 1)));

        Should.Throw<InvalidOperationException>(() =>
            options.AddScheduleCalendar(" holidays ", calendar => calendar.OnDates(new DateOnly(2026, 12, 25))));
    }

    [Fact]
    public void Should_refuse_an_empty_calendar_callback()
    {
        Should.Throw<InvalidOperationException>(() =>
            new EverTaskServiceConfiguration().AddScheduleCalendar("holidays", _ => { }));
    }

    [Fact]
    public void Should_refuse_an_oversized_or_all_days_calendar_at_registration()
    {
        var dates = Enumerable.Range(0, 1001)
            .Select(offset => new DateOnly(2026, 1, 1).AddDays(offset))
            .ToArray();

        Should.Throw<InvalidOperationException>(() =>
            new EverTaskServiceConfiguration().AddScheduleCalendar("oversized", calendar => calendar.OnDates(dates)));
        Should.Throw<InvalidOperationException>(() =>
            new EverTaskServiceConfiguration().AddScheduleCalendar("all-days",
                calendar => calendar.OnDays(Enum.GetValues<DayOfWeek>())));
    }

    [Fact]
    public void Should_register_a_deep_frozen_canonical_snapshot_immediately_after_configuration()
    {
        EverTaskServiceConfiguration? captured = null;
        var services = new ServiceCollection();
        services.AddEverTask(options =>
        {
            captured = options;
            options.RegisterTasksFromAssembly(typeof(ScheduleCalendarRegistrationTests).Assembly)
                .AddScheduleCalendar(" holidays ", calendar => calendar
                    .OnDays(DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Friday)
                    .OnDates(new DateOnly(2026, 12, 25), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25))
                    .Between(
                        new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)),
                        new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(2)))
                    .Between(
                        new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)));
        });

        captured.ShouldNotBeNull().AddScheduleCalendar(
            "late", calendar => calendar.OnDates(new DateOnly(2027, 1, 1)));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ScheduleCalendarRegistry>();
        var resolved = registry.Resolve(new ScheduleExclusions { Calendars = [" holidays "] });

        resolved.Calendars.ShouldBeEmpty();
        resolved.Days.ShouldBe([DayOfWeek.Monday, DayOfWeek.Friday]);
        resolved.Dates.ShouldBe([new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25)]);
        resolved.Ranges.ShouldHaveSingleItem().FromUtc.ShouldBe(
            new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
        resolved.Ranges.ShouldHaveSingleItem().ToUtc.ShouldBe(
            new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero));
        Should.Throw<ArgumentException>(() =>
            registry.Resolve(new ScheduleExclusions { Calendars = ["late"] }));
    }

    [Fact]
    public void Should_refuse_more_than_sixteen_names_before_lookup()
    {
        var registry = BuildRegistry(_ => { });
        var exclusions = new ScheduleExclusions
        {
            Calendars = Enumerable.Range(0, 17).Select(index => $"missing-{index}").ToArray()
        };

        var refusal = Should.Throw<InvalidOperationException>(() => registry.Resolve(exclusions));

        refusal.Message.ShouldContain("16");
    }

    [Fact]
    public void Should_refuse_a_resolved_union_over_the_entry_cap()
    {
        var first = Enumerable.Range(0, 501).Select(offset => new DateOnly(2020, 1, 1).AddDays(offset)).ToArray();
        var second = Enumerable.Range(501, 500).Select(offset => new DateOnly(2020, 1, 1).AddDays(offset)).ToArray();
        var registry = BuildRegistry(options => options
            .AddScheduleCalendar("first", calendar => calendar.OnDates(first))
            .AddScheduleCalendar("second", calendar => calendar.OnDates(second)));

        Should.Throw<InvalidOperationException>(() => registry.Resolve(new ScheduleExclusions
        {
            Dates = [new DateOnly(2030, 1, 1)],
            Calendars = ["first", "second"]
        }));
    }

    [Fact]
    public void Should_refuse_a_resolved_union_covering_all_days()
    {
        var registry = BuildRegistry(options => options
            .AddScheduleCalendar("first", calendar => calendar.OnDays(
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday))
            .AddScheduleCalendar("second", calendar => calendar.OnDays(
                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday)));

        Should.Throw<InvalidOperationException>(() => registry.Resolve(new ScheduleExclusions
        {
            Calendars = ["first", "second"]
        }));
    }

    private static ScheduleCalendarRegistry BuildRegistry(Action<EverTaskServiceConfiguration> configure)
    {
        var services = new ServiceCollection();
        services.AddEverTask(options =>
        {
            options.RegisterTasksFromAssembly(typeof(ScheduleCalendarRegistrationTests).Assembly);
            configure(options);
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ScheduleCalendarRegistry>();
    }
}

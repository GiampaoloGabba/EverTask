using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

public class ScheduleExclusionValidationTests
{
    [Fact]
    public void Should_normalize_exclusions_to_one_canonical_form()
    {
        var task = NewTask(new ScheduleExclusions
        {
            Days = [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Friday],
            Dates = [new DateOnly(2026, 12, 25), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25)],
            Ranges =
            [
                Range("2026-06-01T11:00:00+00:00", "2026-06-01T12:00:00+00:00"),
                Range("2026-06-01T10:00:00+02:00", "2026-06-01T12:00:00+02:00"),
                Range("2026-06-01T09:00:00+00:00", "2026-06-01T11:00:00+00:00"),
                Range("2026-07-01T20:00:00+00:00", "2026-07-01T21:00:00+00:00")
            ]
        });

        task.Validate();

        var exclusions = task.Exclusions.ShouldNotBeNull();
        exclusions.Days.ShouldBe([DayOfWeek.Monday, DayOfWeek.Friday]);
        exclusions.Dates.ShouldBe([new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25)]);
        exclusions.Ranges.Length.ShouldBe(2);
        exclusions.Ranges[0].FromUtc.ShouldBe(DateTimeOffset.Parse("2026-06-01T08:00:00+00:00"));
        exclusions.Ranges[0].ToUtc.ShouldBe(DateTimeOffset.Parse("2026-06-01T12:00:00+00:00"));
        exclusions.Ranges[1].FromUtc.Offset.ShouldBe(TimeSpan.Zero);
        exclusions.Ranges[1].ToUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Should_normalize_null_or_empty_arrays_to_no_exclusions()
    {
        var task = NewTask(new ScheduleExclusions
        {
            Days = null!, Dates = null!, Ranges = null!, Calendars = null!
        });

        task.Validate();

        task.Exclusions.ShouldBeNull();
    }

    [Fact]
    public void Should_keep_and_canonicalize_calendar_only_exclusions()
    {
        var task = NewTask(new ScheduleExclusions
        {
            Calendars = [" holidays ", "maintenance", "holidays"]
        });

        task.Validate();

        task.Exclusions.ShouldNotBeNull().Calendars.ShouldBe(["holidays", "maintenance"]);
    }

    [Fact]
    public void Should_refuse_corrupt_or_oversized_calendar_references()
    {
        var nullName = NewTask(new ScheduleExclusions { Calendars = [null!] });
        var blankName = NewTask(new ScheduleExclusions { Calendars = [" "] });
        var longName = NewTask(new ScheduleExclusions { Calendars = [new string('x', 101)] });
        var tooMany = NewTask(new ScheduleExclusions
        {
            Calendars = Enumerable.Range(0, 17).Select(index => $"calendar-{index}").ToArray()
        });

        Should.Throw<ArgumentException>(() => nullName.Validate());
        Should.Throw<ArgumentException>(() => blankName.Validate());
        Should.Throw<ArgumentException>(() => longName.Validate());
        Should.Throw<InvalidOperationException>(() => tooMany.Validate());
    }

    [Fact]
    public void Should_resolve_named_calendars_when_a_validation_context_is_available()
    {
        var services = new ServiceCollection();
        services.AddEverTask(options => options
            .RegisterTasksFromAssembly(typeof(ScheduleExclusionValidationTests).Assembly)
            .AddScheduleCalendar("holidays", calendar => calendar.OnDates(new DateOnly(2026, 12, 25))));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ScheduleCalendarRegistry>();
        var known = NewTask(new ScheduleExclusions { Calendars = ["holidays"] });
        var unknown = NewTask(new ScheduleExclusions { Calendars = ["missing"] });

        Should.NotThrow(() => known.Validate(new ScheduleValidationContext(null, registry)));
        known.Exclusions.ShouldNotBeNull().Calendars.ShouldBe(["holidays"]);
        known.Exclusions.ShouldNotBeNull().Dates.ShouldBeEmpty("validation judges the resolved union but persists names");
        Should.Throw<ArgumentException>(() => unknown.Validate(new ScheduleValidationContext(null, registry)))
            .Message.ShouldContain("missing");
    }

    [Fact]
    public void Should_apply_the_definition_cap_after_deduplication()
    {
        var repeated = NewTask(new ScheduleExclusions
        {
            Dates = Enumerable.Repeat(new DateOnly(2026, 1, 1), 1001).ToArray()
        });
        var oversized = NewTask(new ScheduleExclusions
        {
            Dates = Enumerable.Range(0, 1001).Select(day => new DateOnly(2026, 1, 1).AddDays(day)).ToArray()
        });

        Should.NotThrow(() => repeated.Validate());
        repeated.Exclusions.ShouldNotBeNull().Dates.ShouldHaveSingleItem();
        Should.Throw<InvalidOperationException>(() => oversized.Validate());
    }

    [Fact]
    public void Should_refuse_exclusions_that_cover_every_day()
    {
        var task = NewTask(new ScheduleExclusions { Days = Enum.GetValues<DayOfWeek>() });

        Should.Throw<InvalidOperationException>(() => task.Validate());
    }

    [Fact]
    public void Should_refuse_exclusions_beside_an_occurrence_provider()
    {
        var task = new RecurringTask
        {
            Provider = new ProviderSettings { Key = "calendar" },
            Exclusions = new ScheduleExclusions { Dates = [new DateOnly(2026, 12, 25)] }
        };

        Should.Throw<InvalidOperationException>(() => task.Validate());
    }

    [Fact]
    public void Should_refuse_corrupt_exclusion_metadata()
    {
        var reversedRange = NewTask(new ScheduleExclusions
        {
            Ranges = [Range("2026-01-02T00:00:00+00:00", "2026-01-01T00:00:00+00:00")]
        });
        var nullRange = NewTask(new ScheduleExclusions { Ranges = [null!] });
        var invalidDay = NewTask(new ScheduleExclusions { Days = [(DayOfWeek)99] });

        Should.Throw<ArgumentException>(() => reversedRange.Validate());
        Should.Throw<ArgumentException>(() => nullRange.Validate());
        Should.Throw<ArgumentException>(() => invalidDay.Validate());
    }

    private static RecurringTask NewTask(ScheduleExclusions exclusions) => new()
    {
        SecondInterval = new SecondInterval(30),
        Exclusions = exclusions
    };

    private static ExclusionRange Range(string from, string to) => new()
    {
        FromUtc = DateTimeOffset.Parse(from),
        ToUtc = DateTimeOffset.Parse(to)
    };
}

using Xunit;

namespace EverTask.Analyzers.Tests;

/// <summary>
/// ET0010: <c>InTimeZone</c> on a fluent chain that is provably a plain cadence. The EverTask types are
/// source stubs: the analyzer resolves them by metadata name, so their shape only needs to match the real
/// signatures.
/// </summary>
public class Et0010ScheduleTimeZoneTests
{
    private static Task VerifyAsync(string source) =>
        CSharpAnalyzerVerifier<Analyzers.ScheduleTimeZoneAnalyzer>.VerifyAsync(source, extraSource: Stubs);

    private static Task VerifyChainAsync(string chain) => VerifyAsync("""
        using System;
        using EverTask.Abstractions;
        class C
        {
            void M(IRecurringTaskBuilder r) => CHAIN;
        }
        """.Replace("CHAIN", chain));

    [Theory]
    // every elapsed-anchoring selector, plus the alignments that only re-phase one
    [InlineData("""r.Schedule().EverySecond().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    [InlineData("""r.Schedule().EveryMinute().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    [InlineData("""r.Schedule().EveryHour().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    [InlineData("""r.Schedule().Every(30).Seconds().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    [InlineData("""r.Schedule().Every(30).Minutes().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    [InlineData("""r.Schedule().Every(2).Hours().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    [InlineData("""r.Schedule().EveryHour().AtMinute(30).{|ET0010:InTimeZone("Asia/Kolkata")|}""")]
    [InlineData("""r.Schedule().EveryMinute().AtSecond(15).{|ET0010:InTimeZone("Europe/Rome")|}""")]
    // the TimeZoneInfo overload, a zone named before a bound, and the chain that starts with a first run
    [InlineData("""r.Schedule().Every(5).Minutes().{|ET0010:InTimeZone(TimeZoneInfo.Utc)|}""")]
    [InlineData("""r.Schedule().EveryMinute().WithDurableOccurrences().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    [InlineData("""r.RunNow().Then().EveryMinute().{|ET0010:InTimeZone("Europe/Rome")|}""")]
    public Task Reports_a_time_zone_on_an_elapsed_chain(string chain) => VerifyChainAsync(chain);

    [Theory]
    // a Day, Week or Month interval is calendar-anchored on its own - it snaps to a time of day
    [InlineData("""r.Schedule().EveryDay().InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryWeek().InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryMonth().InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().Every(3).Days().InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().Every(2).Weeks().InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().Every(2).Months().InTimeZone("Europe/Rome")""")]
    // every calendar selector
    [InlineData("""r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryDay().AtTimes(new TimeOnly(9, 0)).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().OnDays(DayOfWeek.Monday).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().OnMonths(1, 7).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryMonth().OnDay(3).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryMonth().OnFirst(DayOfWeek.Monday).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().UseCron("0 9 * * *").InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().UseOccurrenceProvider("business-days").InTimeZone("Europe/Rome")""")]
    // named before the shape exists: the calendar selector that follows is what decides
    [InlineData("""r.Schedule().InTimeZone("Europe/Rome").EveryDay().AtTime(new TimeOnly(9, 0))""")]
    [InlineData("""r.Schedule().InTimeZone("Europe/Rome").EveryMinute()""")]
    [InlineData("""r.RunAt(DateTimeOffset.UtcNow).Then().EveryDay().InTimeZone("Europe/Rome")""")]
    public Task Reports_nothing_on_a_calendar_chain(string chain) => VerifyChainAsync(chain);

    [Theory]
    [InlineData("""r.Schedule().EveryHour().ExceptWeekends().InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryHour().InTimeZone("Europe/Rome").ExceptWeekends()""")]
    [InlineData("""r.Schedule().EveryHour().Except(e => e.OnDays(DayOfWeek.Saturday)).InTimeZone("Europe/Rome")""")]
    [InlineData("""r.Schedule().EveryHour().InTimeZone("Europe/Rome").Except(e => e.OnDates(new DateOnly(2026, 12, 25)))""")]
    public Task Should_not_report_when_an_exclusion_appears_anywhere_in_the_completed_chain(string chain) =>
        VerifyChainAsync(chain);

    [Fact]
    public Task Reports_nothing_when_the_chain_is_split_over_a_variable() => VerifyAsync("""
        using System;
        using EverTask.Abstractions;
        class C
        {
            void M(IRecurringTaskBuilder r)
            {
                var builder = r.Schedule().EveryMinute();
                builder.InTimeZone("Europe/Rome");
                builder.AtSecond(15).InTimeZone("Europe/Rome");
            }
        }
        """);

    [Fact]
    public Task Reports_nothing_when_the_chain_comes_from_a_method() => VerifyAsync("""
        using System;
        using EverTask.Abstractions;
        class C
        {
            IMinuteSchedulerBuilder Cadence(IRecurringTaskBuilder r) => r.Schedule().EveryMinute();
            void M(IRecurringTaskBuilder r) => Cadence(r).InTimeZone("Europe/Rome");
            void N(IRecurringTaskBuilder r) => Cadence(r).AtSecond(15).InTimeZone("Europe/Rome");
        }
        """);

    [Fact]
    public Task Reports_nothing_when_a_second_chain_configures_the_same_builder() => VerifyAsync("""
        using System;
        using EverTask.Abstractions;
        class C
        {
            void M(IRecurringTaskBuilder r)
            {
                r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0));
                r.Schedule().EveryMinute().InTimeZone("Europe/Rome");
            }
        }
        """);

    [Fact]
    public Task Reports_nothing_for_an_unrelated_InTimeZone() => VerifyAsync("""
        class Other { public Other InTimeZone(string id) => this; }
        class C
        {
            void M(Other o) => o.InTimeZone("Europe/Rome");
        }
        """);

    [Fact]
    public Task Reports_an_elapsed_chain_inside_a_dispatch_lambda() => VerifyAsync("""
        using System;
        using EverTask.Abstractions;
        class C
        {
            void Dispatch(object task, Action<IRecurringTaskBuilder> schedule) { }
            void M() =>
                Dispatch(new object(), r => r.Schedule().Every(30).Minutes().{|ET0010:InTimeZone("Europe/Rome")|});
        }
        """);

    // Minimal mirrors of the real fluent surface, matching full metadata names. `MaxRuns` returns void and
    // `OnHours` is off the interface in the real API, so neither can appear mid-chain.
    private const string Stubs = """
        using System;
        namespace EverTask.Abstractions
        {
            public interface IRecurringTaskBuilder
            {
                IThenableSchedulerBuilder RunNow();
                IThenableSchedulerBuilder RunDelayed(TimeSpan delay);
                IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset);
                IIntervalSchedulerBuilder Schedule();
            }
            public interface IThenableSchedulerBuilder { IIntervalSchedulerBuilder Then(); }
            public interface IExclusionBuilder
            {
                IExclusionBuilder OnDays(params DayOfWeek[] days);
                IExclusionBuilder OnDates(params DateOnly[] dates);
            }
            public interface IIntervalSchedulerBuilder
            {
                IBuildableSchedulerBuilder UseCron(string cronExpression);
                IBuildableSchedulerBuilder UseOccurrenceProvider(string key, string config = null);
                IEverySchedulerBuilder Every(int number);
                IBuildableSchedulerBuilder EverySecond();
                IMinuteSchedulerBuilder EveryMinute();
                IHourSchedulerBuilder EveryHour();
                IDailyTimeSchedulerBuilder EveryDay();
                IWeeklySchedulerBuilder EveryWeek();
                IMonthlySchedulerBuilder EveryMonth();
                IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days);
                IMonthlySchedulerBuilder OnMonths(params int[] months);
                IIntervalSchedulerBuilder InTimeZone(TimeZoneInfo timeZone);
                IIntervalSchedulerBuilder InTimeZone(string timeZoneId);
                IIntervalSchedulerBuilder WithDurableOccurrences();
                IIntervalSchedulerBuilder BackfillFrom(DateTimeOffset startUtc);
                IIntervalSchedulerBuilder Except(Action<IExclusionBuilder> configure);
                IIntervalSchedulerBuilder ExceptWeekends();
            }
            public interface IEverySchedulerBuilder
            {
                IBuildableSchedulerBuilder Seconds();
                IMinuteSchedulerBuilder Minutes();
                IHourSchedulerBuilder Hours();
                IDailyTimeSchedulerBuilder Days();
                IWeeklySchedulerBuilder Weeks();
                IMonthlySchedulerBuilder Months();
            }
            public interface IHourSchedulerBuilder
            {
                IMinuteSchedulerBuilder AtMinute(int minute);
                IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
                void MaxRuns(int maxRuns);
                IHourSchedulerBuilder InTimeZone(TimeZoneInfo timeZone);
                IHourSchedulerBuilder InTimeZone(string timeZoneId);
                IHourSchedulerBuilder WithDurableOccurrences();
                IHourSchedulerBuilder Except(Action<IExclusionBuilder> configure);
                IHourSchedulerBuilder ExceptWeekends();
            }
            public interface IMinuteSchedulerBuilder
            {
                IBuildableSchedulerBuilder AtSecond(int second);
                IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
                void MaxRuns(int maxRuns);
                IMinuteSchedulerBuilder InTimeZone(TimeZoneInfo timeZone);
                IMinuteSchedulerBuilder InTimeZone(string timeZoneId);
                IMinuteSchedulerBuilder WithDurableOccurrences();
                IMinuteSchedulerBuilder Except(Action<IExclusionBuilder> configure);
                IMinuteSchedulerBuilder ExceptWeekends();
            }
            public interface IBuildableSchedulerBuilder
            {
                IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
                void MaxRuns(int maxRuns);
                IBuildableSchedulerBuilder InTimeZone(TimeZoneInfo timeZone);
                IBuildableSchedulerBuilder InTimeZone(string timeZoneId);
                IBuildableSchedulerBuilder WithDurableOccurrences();
                IBuildableSchedulerBuilder Except(Action<IExclusionBuilder> configure);
                IBuildableSchedulerBuilder ExceptWeekends();
            }
            public interface IDailyTimeSchedulerBuilder : IBuildableSchedulerBuilder
            {
                IBuildableSchedulerBuilder AtTime(TimeOnly time);
                IBuildableSchedulerBuilder AtTimes(params TimeOnly[] times);
                new IDailyTimeSchedulerBuilder InTimeZone(TimeZoneInfo timeZone);
                new IDailyTimeSchedulerBuilder InTimeZone(string timeZoneId);
            }
            public interface IWeeklySchedulerBuilder : IBuildableSchedulerBuilder
            {
                IDailyTimeSchedulerBuilder OnDay(DayOfWeek day);
                IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days);
                new IWeeklySchedulerBuilder InTimeZone(TimeZoneInfo timeZone);
                new IWeeklySchedulerBuilder InTimeZone(string timeZoneId);
            }
            public interface IMonthlySchedulerBuilder : IBuildableSchedulerBuilder
            {
                IDailyTimeSchedulerBuilder OnDay(int day);
                IDailyTimeSchedulerBuilder OnDays(params int[] day);
                IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day);
                new IMonthlySchedulerBuilder InTimeZone(TimeZoneInfo timeZone);
                new IMonthlySchedulerBuilder InTimeZone(string timeZoneId);
            }
        }
        """;
}

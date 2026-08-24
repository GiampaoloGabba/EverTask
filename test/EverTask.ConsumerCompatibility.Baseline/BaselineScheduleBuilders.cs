using EverTask.Abstractions;

namespace EverTask.ConsumerCompatibility.Baseline;

/// <summary>
/// The schedule builder interfaces implemented from OUTSIDE the library, exactly as the baseline declared
/// them — the shape an application takes when it wraps or replaces EverTask's fluent API.
/// </summary>
/// <remarks>
/// <para>
/// This is the binary half of T3/P6 for the builders. <c>InTimeZone</c> arrived on eight of these interfaces
/// as a default interface member so an outside implementation would neither have to declare it nor be
/// recompiled; recompiling a probe against the new sources proves only that it compiles. The CLR builds this
/// type's interface map against TODAY's interfaces, so a member that had arrived abstract instead fails the
/// type load the moment the type is constructed — which is what the test asserts, together with the default
/// bodies really being reachable through every one of the eight slots.
/// </para>
/// <para>
/// One type implements all of them, returning itself, because what is under test is the interface map and not
/// a schedule: nothing here builds a definition. The recorded call names exist so the probe cannot be
/// optimised down to a constructor call.
/// </para>
/// </remarks>
public sealed class BaselineScheduleBuilders :
    IRecurringTaskBuilder,
    IIntervalSchedulerBuilder,
    IEverySchedulerBuilder,
    IHourSchedulerBuilder,
    IMinuteSchedulerBuilder,
    IDailyTimeSchedulerBuilder,
    IWeeklySchedulerBuilder,
    IMonthlySchedulerBuilder,
    IThenableSchedulerBuilder,
    IBuildableSchedulerBuilder
{
    public List<string> Calls { get; } = [];

    private BaselineScheduleBuilders Record(string call)
    {
        Calls.Add(call);
        return this;
    }

    // IRecurringTaskBuilder
    public IThenableSchedulerBuilder RunNow() => Record(nameof(RunNow));
    public IThenableSchedulerBuilder RunDelayed(TimeSpan delay) => Record(nameof(RunDelayed));
    public IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset) => Record(nameof(RunAt));
    public IIntervalSchedulerBuilder Schedule() => Record(nameof(Schedule));

    // IThenableSchedulerBuilder
    public IIntervalSchedulerBuilder Then() => Record(nameof(Then));

    // IIntervalSchedulerBuilder
    public IBuildableSchedulerBuilder UseCron(string cronExpression) => Record(nameof(UseCron));
    public IEverySchedulerBuilder Every(int number) => Record(nameof(Every));
    public IBuildableSchedulerBuilder EverySecond() => Record(nameof(EverySecond));
    public IMinuteSchedulerBuilder EveryMinute() => Record(nameof(EveryMinute));
    public IHourSchedulerBuilder EveryHour() => Record(nameof(EveryHour));
    public IDailyTimeSchedulerBuilder EveryDay() => Record(nameof(EveryDay));
    public IWeeklySchedulerBuilder EveryWeek() => Record(nameof(EveryWeek));
    public IMonthlySchedulerBuilder EveryMonth() => Record(nameof(EveryMonth));
    public IMonthlySchedulerBuilder OnMonths(params int[] months) => Record(nameof(OnMonths));

    // Shared by IIntervalSchedulerBuilder and IWeeklySchedulerBuilder: same signature, same return type.
    public IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days) => Record("OnDays(DayOfWeek[])");

    // IEverySchedulerBuilder
    public IBuildableSchedulerBuilder Seconds() => Record(nameof(Seconds));
    public IMinuteSchedulerBuilder Minutes() => Record(nameof(Minutes));
    public IHourSchedulerBuilder Hours() => Record(nameof(Hours));
    public IDailyTimeSchedulerBuilder Days() => Record(nameof(Days));
    public IWeeklySchedulerBuilder Weeks() => Record(nameof(Weeks));
    public IMonthlySchedulerBuilder Months() => Record(nameof(Months));

    // IHourSchedulerBuilder / IMinuteSchedulerBuilder
    public IMinuteSchedulerBuilder AtMinute(int minute) => Record(nameof(AtMinute));
    public IBuildableSchedulerBuilder AtSecond(int second) => Record(nameof(AtSecond));

    // IDailyTimeSchedulerBuilder
    public IBuildableSchedulerBuilder AtTime(TimeOnly time) => Record(nameof(AtTime));
    public IBuildableSchedulerBuilder AtTimes(params TimeOnly[] times) => Record(nameof(AtTimes));

    // IWeeklySchedulerBuilder
    public IDailyTimeSchedulerBuilder OnDay(DayOfWeek day) => Record("OnDay(DayOfWeek)");

    // IMonthlySchedulerBuilder
    public IDailyTimeSchedulerBuilder OnDay(int day) => Record("OnDay(int)");
    public IDailyTimeSchedulerBuilder OnDays(params int[] day) => Record("OnDays(int[])");
    public IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day) => Record(nameof(OnFirst));

    // IBuildableSchedulerBuilder, also reached through IHourSchedulerBuilder and IMinuteSchedulerBuilder
    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset) => Record(nameof(RunUntil));
    public void MaxRuns(int maxRuns) => Record(nameof(MaxRuns));
}

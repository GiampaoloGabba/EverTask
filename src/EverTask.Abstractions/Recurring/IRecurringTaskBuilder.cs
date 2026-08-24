using System.Runtime.CompilerServices;

namespace EverTask.Abstractions;

public interface IRecurringTaskBuilder
{
    IThenableSchedulerBuilder RunNow();
    IThenableSchedulerBuilder RunDelayed(TimeSpan delay);
    IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset);

    IIntervalSchedulerBuilder Schedule();
}

public interface IIntervalSchedulerBuilder
{
    IBuildableSchedulerBuilder UseCron(string cronExpression);

    IEverySchedulerBuilder Every(int number);

    IBuildableSchedulerBuilder EverySecond();
    IMinuteSchedulerBuilder EveryMinute();
    IHourSchedulerBuilder EveryHour();
    IDailyTimeSchedulerBuilder EveryDay();
    IWeeklySchedulerBuilder EveryWeek();
    IMonthlySchedulerBuilder EveryMonth();

    IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days);
    IMonthlySchedulerBuilder OnMonths(params int[] months);

    /// <inheritdoc cref="IBuildableSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    IIntervalSchedulerBuilder InTimeZone(TimeZoneInfo timeZone) => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.InTimeZone(string)"/>
    IIntervalSchedulerBuilder InTimeZone(string timeZoneId) => throw SchedulerBuilderDefaults.NotImplemented();
}

/// <summary>
/// What the default bodies of the schedule builder interfaces do. Every member added after 3.11 arrives as a
/// default interface member so an outside implementation of these interfaces keeps compiling (T3/P6) — and
/// throws here rather than silently doing nothing, because a schedule that quietly dropped its time zone
/// would run at the wrong hour instead of failing.
/// </summary>
internal static class SchedulerBuilderDefaults
{
    internal static NotSupportedException NotImplemented([CallerMemberName] string member = "") =>
        new($"This schedule builder does not implement '{member}'. The builders EverTask hands to " +
            "Dispatch(task, r => ...) do; a custom implementation of the builder interfaces has to " +
            "implement it itself.");
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

    /// <inheritdoc cref="IBuildableSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    IHourSchedulerBuilder InTimeZone(TimeZoneInfo timeZone) => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.InTimeZone(string)"/>
    IHourSchedulerBuilder InTimeZone(string timeZoneId) => throw SchedulerBuilderDefaults.NotImplemented();
}

public interface IMinuteSchedulerBuilder
{
    IBuildableSchedulerBuilder AtSecond(int second);
    IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
    void MaxRuns(int maxRuns);

    /// <inheritdoc cref="IBuildableSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    IMinuteSchedulerBuilder InTimeZone(TimeZoneInfo timeZone) => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.InTimeZone(string)"/>
    IMinuteSchedulerBuilder InTimeZone(string timeZoneId) => throw SchedulerBuilderDefaults.NotImplemented();
}

public interface IDailyTimeSchedulerBuilder : IBuildableSchedulerBuilder
{
    IBuildableSchedulerBuilder AtTime(TimeOnly time);
    IBuildableSchedulerBuilder AtTimes(params TimeOnly[] times);

    /// <inheritdoc cref="IBuildableSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    /// <remarks>
    /// Declared again here, returning this builder, so naming the zone in the middle of the chain does not
    /// consume the refinement that follows it: <c>EveryDay().InTimeZone(z).AtTime(...)</c> is exactly the
    /// schedule the zone exists for, and the inherited overload would have ended the chain at
    /// <see cref="IBuildableSchedulerBuilder"/>, where <c>AtTime</c> no longer exists.
    /// </remarks>
    new IDailyTimeSchedulerBuilder InTimeZone(TimeZoneInfo timeZone) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="InTimeZone(TimeZoneInfo)"/>
    new IDailyTimeSchedulerBuilder InTimeZone(string timeZoneId) =>
        throw SchedulerBuilderDefaults.NotImplemented();
}

/// <summary>
/// Builder for configuring weekly recurring tasks
/// </summary>
public interface IWeeklySchedulerBuilder : IBuildableSchedulerBuilder
{
    /// <summary>
    /// Specifies the task should run on a specific day of the week
    /// </summary>
    IDailyTimeSchedulerBuilder OnDay(DayOfWeek day);

    /// <summary>
    /// Specifies the task should run on specific days of the week
    /// </summary>
    IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days);

    /// <inheritdoc cref="IDailyTimeSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    new IWeeklySchedulerBuilder InTimeZone(TimeZoneInfo timeZone) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IDailyTimeSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    new IWeeklySchedulerBuilder InTimeZone(string timeZoneId) =>
        throw SchedulerBuilderDefaults.NotImplemented();
}

public interface IMonthlySchedulerBuilder : IBuildableSchedulerBuilder
{
    IDailyTimeSchedulerBuilder OnDay(int day);
    IDailyTimeSchedulerBuilder OnDays(params int[] day);
    IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day);

    /// <inheritdoc cref="IDailyTimeSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    new IMonthlySchedulerBuilder InTimeZone(TimeZoneInfo timeZone) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IDailyTimeSchedulerBuilder.InTimeZone(TimeZoneInfo)"/>
    new IMonthlySchedulerBuilder InTimeZone(string timeZoneId) =>
        throw SchedulerBuilderDefaults.NotImplemented();
}

public interface IThenableSchedulerBuilder
{
    IIntervalSchedulerBuilder Then();
}

public interface IBuildableSchedulerBuilder
{
    IBuildableSchedulerBuilder RunUntil(DateTimeOffset dateTimeOffset);
    void MaxRuns(int maxRuns);

    /// <summary>
    /// Reads this schedule's calendar on <paramref name="timeZone"/>'s clock instead of UTC: 09:00 means
    /// 09:00 there, all year, across every daylight-saving change.
    /// </summary>
    /// <param name="timeZone">
    /// A system time zone. Its IANA id is what gets persisted, so the schedule resolves the same way on
    /// Windows and on Linux; a zone built with <c>TimeZoneInfo.CreateCustomTimeZone</c> has no such id and is
    /// refused.
    /// </param>
    /// <exception cref="ArgumentException">The zone cannot be persisted as an IANA id.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the schedule is built: a plain cadence (every N seconds/minutes/hours) is a constant step
    /// in elapsed time and produces the same instants in every zone, so a zone on it is refused rather than
    /// silently ignored. Anchor the schedule to a calendar — a time of day, a day of the week, a month
    /// selector or a cron expression — or drop the call.
    /// </exception>
    /// <remarks>
    /// A repeated hour (daylight saving ending) fires once, on its first pass. A time of day that a gap
    /// removes (daylight saving starting) fires at the first local time that does exist, and several slots
    /// inside one gap collapse into a single occurrence.
    /// </remarks>
    IBuildableSchedulerBuilder InTimeZone(TimeZoneInfo timeZone) => throw SchedulerBuilderDefaults.NotImplemented();

    /// <summary>
    /// <see cref="InTimeZone(TimeZoneInfo)"/> by id, in either the IANA (<c>Europe/Rome</c>) or the Windows
    /// (<c>W. Europe Standard Time</c>) spelling. The IANA form is what gets persisted either way.
    /// </summary>
    /// <exception cref="ArgumentException">This system cannot resolve the id.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the schedule is built, for a plain cadence — see <see cref="InTimeZone(TimeZoneInfo)"/>.
    /// </exception>
    IBuildableSchedulerBuilder InTimeZone(string timeZoneId) => throw SchedulerBuilderDefaults.NotImplemented();
}

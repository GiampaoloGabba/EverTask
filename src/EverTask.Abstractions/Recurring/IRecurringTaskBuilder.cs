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

    /// <inheritdoc cref="IBuildableSchedulerBuilder.OnMisfire"/>
    IIntervalSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.WithDurableOccurrences"/>
    IIntervalSchedulerBuilder WithDurableOccurrences() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.BackfillFrom"/>
    IIntervalSchedulerBuilder BackfillFrom(DateTimeOffset startUtc) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.Except"/>
    IIntervalSchedulerBuilder Except(Action<IExclusionBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.ExceptWeekends"/>
    IIntervalSchedulerBuilder ExceptWeekends() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <summary>
    /// Takes this schedule's occurrences from a registered <see cref="INextOccurrenceProvider"/> instead of
    /// from a cron expression or an interval — for the calendars the fluent API cannot express (business days,
    /// a holiday table, hours the application keeps in its own database).
    /// </summary>
    /// <param name="key">
    /// The key the provider was registered under with <c>AddOccurrenceProvider&lt;T&gt;(key)</c>. Only the key
    /// is persisted, never a type name.
    /// </param>
    /// <param name="config">
    /// An opaque string handed back to the provider on every call, for a schedule that needs to say WHICH
    /// calendar it means. EverTask never reads it; versioning its format is the provider's business.
    /// </param>
    /// <returns>The builder, for the bounds and the misfire policy.</returns>
    /// <exception cref="ArgumentException">
    /// The key is empty, or — when the schedule is built — no provider is registered under it.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the schedule is built: a provider replaces the grid, so it cannot be combined with a cron
    /// expression or an interval.
    /// </exception>
    /// <remarks>
    /// Everything else works as it does for any other schedule: <c>RunUntil</c>, <c>MaxRuns</c>, the misfire
    /// policies and durable occurrences all go through the same seam. The two exceptions are stated where they
    /// apply — <see cref="CatchUpOverflowPolicy.SkipOldest"/> needs
    /// <see cref="INextOccurrenceProvider.IsDeterministic"/>, and
    /// <see cref="RescheduleMode.RebaseFromCursor"/> is refused because a provider exposes no nominal period.
    /// </remarks>
    IBuildableSchedulerBuilder UseOccurrenceProvider(string key, string? config = null) =>
        throw SchedulerBuilderDefaults.NotImplemented();
}

/// <summary>
/// What the default bodies of the schedule builder interfaces do. Every member added after 3.11 arrives as a
/// default interface member so an outside implementation of these interfaces keeps compiling — and throws
/// here rather than silently doing nothing, because a schedule that quietly dropped its time zone would run
/// at the wrong hour instead of failing.
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

    /// <inheritdoc cref="IBuildableSchedulerBuilder.OnMisfire"/>
    IHourSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.WithDurableOccurrences"/>
    IHourSchedulerBuilder WithDurableOccurrences() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.BackfillFrom"/>
    IHourSchedulerBuilder BackfillFrom(DateTimeOffset startUtc) => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.Except"/>
    IHourSchedulerBuilder Except(Action<IExclusionBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.ExceptWeekends"/>
    IHourSchedulerBuilder ExceptWeekends() => throw SchedulerBuilderDefaults.NotImplemented();
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

    /// <inheritdoc cref="IBuildableSchedulerBuilder.OnMisfire"/>
    IMinuteSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.WithDurableOccurrences"/>
    IMinuteSchedulerBuilder WithDurableOccurrences() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.BackfillFrom"/>
    IMinuteSchedulerBuilder BackfillFrom(DateTimeOffset startUtc) => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.Except"/>
    IMinuteSchedulerBuilder Except(Action<IExclusionBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.ExceptWeekends"/>
    IMinuteSchedulerBuilder ExceptWeekends() => throw SchedulerBuilderDefaults.NotImplemented();
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

    /// <inheritdoc cref="IBuildableSchedulerBuilder.OnMisfire"/>
    /// <remarks>Declared again here for the same reason as <see cref="InTimeZone(TimeZoneInfo)"/>.</remarks>
    new IDailyTimeSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.WithDurableOccurrences"/>
    new IDailyTimeSchedulerBuilder WithDurableOccurrences() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.BackfillFrom"/>
    new IDailyTimeSchedulerBuilder BackfillFrom(DateTimeOffset startUtc) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.Except"/>
    new IDailyTimeSchedulerBuilder Except(Action<IExclusionBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.ExceptWeekends"/>
    new IDailyTimeSchedulerBuilder ExceptWeekends() => throw SchedulerBuilderDefaults.NotImplemented();
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

    /// <inheritdoc cref="IBuildableSchedulerBuilder.OnMisfire"/>
    new IWeeklySchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.WithDurableOccurrences"/>
    new IWeeklySchedulerBuilder WithDurableOccurrences() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.BackfillFrom"/>
    new IWeeklySchedulerBuilder BackfillFrom(DateTimeOffset startUtc) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.Except"/>
    new IWeeklySchedulerBuilder Except(Action<IExclusionBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.ExceptWeekends"/>
    new IWeeklySchedulerBuilder ExceptWeekends() => throw SchedulerBuilderDefaults.NotImplemented();
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

    /// <inheritdoc cref="IBuildableSchedulerBuilder.OnMisfire"/>
    new IMonthlySchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.WithDurableOccurrences"/>
    new IMonthlySchedulerBuilder WithDurableOccurrences() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.BackfillFrom"/>
    new IMonthlySchedulerBuilder BackfillFrom(DateTimeOffset startUtc) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.Except"/>
    new IMonthlySchedulerBuilder Except(Action<IExclusionBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <inheritdoc cref="IBuildableSchedulerBuilder.ExceptWeekends"/>
    new IMonthlySchedulerBuilder ExceptWeekends() => throw SchedulerBuilderDefaults.NotImplemented();
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
    /// silently ignored. A day/date exclusion also makes the zone meaningful as its exclusion clock; otherwise
    /// anchor the schedule to a calendar or drop the call.
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
    /// Thrown when the schedule is built, for a plain cadence without day/date exclusions — see
    /// <see cref="InTimeZone(TimeZoneInfo)"/>.
    /// </exception>
    IBuildableSchedulerBuilder InTimeZone(string timeZoneId) => throw SchedulerBuilderDefaults.NotImplemented();

    /// <summary>
    /// Chooses what this schedule does with a slot that came due while nothing was there to run it.
    /// </summary>
    /// <param name="configure">
    /// Picks exactly one policy: <c>m =&gt; m.Skip()</c> (the default), <c>m =&gt; m.FireOnce(...)</c> or
    /// <c>m =&gt; m.CatchUp(new CatchUpOptions(maxAge, maxOccurrences))</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The callback selected no policy, or selected two.</exception>
    /// <remarks>
    /// <c>FireOnce</c> and <c>CatchUp</c> both replay missed work, so both need a durable identity per slot:
    /// choosing either one turns the schedule durable, exactly as <see cref="WithDurableOccurrences"/> does.
    /// Every occurrence then becomes its own row, with its own status, retries and audit trail, and the
    /// schedule row itself stops running the handler.
    /// </remarks>
    IBuildableSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <summary>
    /// Turns every due slot of this schedule into its own durable row, without changing what happens to
    /// MISSED slots (they are still skipped).
    /// </summary>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The schedule row becomes a definition plus a cursor: it never runs the handler, and each occurrence is
    /// a one-shot child row with its own retries, audit trail and rate-limit budget. Use it when you want a
    /// per-occurrence history (or a failed occurrence you can requeue) but no replay of a backlog.
    /// </para>
    /// <para>
    /// Requires a storage that implements the atomic occurrence operations. All the built-in ones do; a custom
    /// storage that does not is refused at dispatch rather than emulated.
    /// </para>
    /// </remarks>
    IBuildableSchedulerBuilder WithDurableOccurrences() => throw SchedulerBuilderDefaults.NotImplemented();

    /// <summary>
    /// Starts the schedule's cursor at the first occurrence on or after <paramref name="startUtc"/> instead of
    /// at the first one after the dispatch, so a durable schedule can replay a window that predates it.
    /// </summary>
    /// <param name="startUtc">The instant to start from, inclusive.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Only a durable schedule can backfill (there is nowhere to put the replayed occurrences otherwise), and
    /// the replay is still bounded by the misfire policy's own caps: a backfill window wider than
    /// <see cref="CatchUpOptions.MaxAge"/> loses the part that falls outside it, reported like any other
    /// skipped slot.
    /// </remarks>
    IBuildableSchedulerBuilder BackfillFrom(DateTimeOffset startUtc) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <summary>Adds fixed days, dates or absolute windows that the recurring grid must not produce.</summary>
    /// <param name="configure">Adds one or more exclusions. Calls are additive, including across callbacks.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The callback adds no exclusion.</exception>
    IBuildableSchedulerBuilder Except(Action<IExclusionBuilder> configure) =>
        throw SchedulerBuilderDefaults.NotImplemented();

    /// <summary>Excludes Saturday and Sunday on the schedule's exclusion clock.</summary>
    /// <returns>The builder, for chaining.</returns>
    IBuildableSchedulerBuilder ExceptWeekends() => throw SchedulerBuilderDefaults.NotImplemented();
}

namespace EverTask.Scheduler.Recurring.Builder;

public class DailyTimeSchedulerBuilder(RecurringTask task, TimeProvider? timeProvider)
    : IDailyTimeSchedulerBuilder
{
    /// <summary>
    /// The constructor the previous release shipped, kept as a real overload so an assembly compiled against
    /// it still binds. Every builder in this namespace keeps its original arity for the same reason.
    /// </summary>
    public DailyTimeSchedulerBuilder(RecurringTask task) : this(task, null) { }

    /// <summary>
    /// Stores <paramref name="time"/> VERBATIM. It is a time of day, read on whatever clock the schedule ends
    /// up on — the zone named by <c>InTimeZone</c>, or UTC when there is none — so the builder has nothing to
    /// convert.
    /// </summary>
    public IBuildableSchedulerBuilder AtTime(TimeOnly time)
    {
        if (task.DayInterval == null && task.WeekInterval == null && task.MonthInterval == null)
            throw new InvalidOperationException("DayInterval, WeekInterval, or MonthInterval must be set");

        if (task.DayInterval != null)
            task.DayInterval.OnTimes = [time];
        else if (task.WeekInterval != null)
            task.WeekInterval.OnTimes = [time];
        else if (task.MonthInterval != null)
            task.MonthInterval.OnTimes = [time];

        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    /// <inheritdoc cref="AtTime"/>
    public IBuildableSchedulerBuilder AtTimes(params TimeOnly[] times)
    {
        if (task.DayInterval == null && task.WeekInterval == null && task.MonthInterval == null)
            throw new InvalidOperationException("DayInterval, WeekInterval, or MonthInterval must be set");

        // Note: OnTimes property setter will sort the array automatically
        var slotTimes = times.Distinct().ToArray();

        if (task.DayInterval != null)
            task.DayInterval.OnTimes = slotTimes;
        else if (task.WeekInterval != null)
            task.WeekInterval.OnTimes = slotTimes;
        else if (task.MonthInterval != null)
            task.MonthInterval.OnTimes = slotTimes;

        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset runUntil)
    {
        var runUntilUtc = runUntil.ToUniversalTime();
        if (runUntilUtc < (timeProvider ?? TimeProvider.System).GetUtcNow())
            throw new InvalidOperationException("RunUntil cannot be in the past");

        task.RunUntil = runUntilUtc;
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public void MaxRuns(int maxRuns) => task.MaxRuns = maxRuns;

    // Returns THIS builder, so the time-of-day refinement survives a zone named mid-chain
    // (EveryDay().InTimeZone(z).AtTime(...)). The inherited IBuildableSchedulerBuilder overload forwards
    // here, so the two spellings build the same definition.
    public IDailyTimeSchedulerBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        task.SetTimeZone(timeZone);
        return this;
    }

    public IDailyTimeSchedulerBuilder InTimeZone(string timeZoneId)
    {
        task.SetTimeZone(timeZoneId);
        return this;
    }

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.InTimeZone(TimeZoneInfo timeZone) =>
        InTimeZone(timeZone);

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.InTimeZone(string timeZoneId) =>
        InTimeZone(timeZoneId);

    // Same shape as InTimeZone above: these return THIS builder so a modifier named mid-chain does not
    // consume the time-of-day refinement that follows it.
    public IDailyTimeSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure)
    {
        ScheduleModifiers.OnMisfire(task, configure);
        return this;
    }

    public IDailyTimeSchedulerBuilder WithDurableOccurrences()
    {
        ScheduleModifiers.WithDurableOccurrences(task);
        return this;
    }

    public IDailyTimeSchedulerBuilder BackfillFrom(DateTimeOffset startUtc)
    {
        ScheduleModifiers.BackfillFrom(task, startUtc);
        return this;
    }

    public IDailyTimeSchedulerBuilder Except(Action<IExclusionBuilder> configure)
    {
        ScheduleModifiers.Except(task, configure);
        return this;
    }

    public IDailyTimeSchedulerBuilder ExceptWeekends()
    {
        ScheduleModifiers.ExceptWeekends(task);
        return this;
    }

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        OnMisfire(configure);

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.WithDurableOccurrences() => WithDurableOccurrences();

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.BackfillFrom(DateTimeOffset startUtc) =>
        BackfillFrom(startUtc);

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.Except(Action<IExclusionBuilder> configure) =>
        Except(configure);

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.ExceptWeekends() => ExceptWeekends();
}

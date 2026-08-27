namespace EverTask.Scheduler.Recurring.Builder;

public class WeeklySchedulerBuilder(RecurringTask task, TimeProvider? timeProvider) : IWeeklySchedulerBuilder
{
    /// <summary>The historical constructor, kept so an assembly compiled against an earlier release binds.</summary>
    public WeeklySchedulerBuilder(RecurringTask task) : this(task, null) { }

    public IDailyTimeSchedulerBuilder OnDay(DayOfWeek day)
    {
        if (task.WeekInterval == null)
            throw new InvalidOperationException("WeekInterval must be set before calling OnDay");

        task.WeekInterval.OnDays = [day];
        return new DailyTimeSchedulerBuilder(task, timeProvider);
    }

    public IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days)
    {
        if (task.WeekInterval == null)
            throw new InvalidOperationException("WeekInterval must be set before calling OnDays");

        task.WeekInterval.OnDays = days.Distinct().ToArray();
        return new DailyTimeSchedulerBuilder(task, timeProvider);
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

    // Returns THIS builder: EveryWeek().InTimeZone(z).OnDay(...) has to keep the day selector reachable.
    public IWeeklySchedulerBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        task.SetTimeZone(timeZone);
        return this;
    }

    public IWeeklySchedulerBuilder InTimeZone(string timeZoneId)
    {
        task.SetTimeZone(timeZoneId);
        return this;
    }

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.InTimeZone(TimeZoneInfo timeZone) =>
        InTimeZone(timeZone);

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.InTimeZone(string timeZoneId) =>
        InTimeZone(timeZoneId);

    public IWeeklySchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure)
    {
        ScheduleModifiers.OnMisfire(task, configure);
        return this;
    }

    public IWeeklySchedulerBuilder WithDurableOccurrences()
    {
        ScheduleModifiers.WithDurableOccurrences(task);
        return this;
    }

    public IWeeklySchedulerBuilder BackfillFrom(DateTimeOffset startUtc)
    {
        ScheduleModifiers.BackfillFrom(task, startUtc);
        return this;
    }

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.OnMisfire(Action<IMisfirePolicyBuilder> configure) =>
        OnMisfire(configure);

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.WithDurableOccurrences() => WithDurableOccurrences();

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.BackfillFrom(DateTimeOffset startUtc) =>
        BackfillFrom(startUtc);
}

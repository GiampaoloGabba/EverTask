namespace EverTask.Scheduler.Recurring.Builder;

public class HourSchedulerBuilder(RecurringTask task, TimeProvider? timeProvider) : IHourSchedulerBuilder
{
    /// <summary>The historical constructor, kept so an assembly compiled against an earlier release binds.</summary>
    public HourSchedulerBuilder(RecurringTask task) : this(task, null) { }

    public IMinuteSchedulerBuilder AtMinute(int minute)
    {
        ArgumentNullException.ThrowIfNull(task.HourInterval);

        if (minute is < 0 or > 59)
            throw new ArgumentOutOfRangeException(nameof(minute));

        task.HourInterval.OnMinute = minute;
        return new MinuteSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset runUntil)
    {
        var runUntilUtc = runUntil.ToUniversalTime();
        if (runUntilUtc < (timeProvider ?? TimeProvider.System).GetUtcNow())
            throw new InvalidOperationException("RunUntil cannot be in the past");

        task.RunUntil = runUntilUtc;
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public void MaxRuns(int maxRuns) =>task.MaxRuns = maxRuns;

    public IHourSchedulerBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        task.SetTimeZone(timeZone);
        return this;
    }

    public IHourSchedulerBuilder InTimeZone(string timeZoneId)
    {
        task.SetTimeZone(timeZoneId);
        return this;
    }

    public IHourSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure)
    {
        ScheduleModifiers.OnMisfire(task, configure);
        return this;
    }

    public IHourSchedulerBuilder WithDurableOccurrences()
    {
        ScheduleModifiers.WithDurableOccurrences(task);
        return this;
    }

    public IHourSchedulerBuilder BackfillFrom(DateTimeOffset startUtc)
    {
        ScheduleModifiers.BackfillFrom(task, startUtc);
        return this;
    }

    public IHourSchedulerBuilder Except(Action<IExclusionBuilder> configure)
    {
        ScheduleModifiers.Except(task, configure);
        return this;
    }

    public IHourSchedulerBuilder ExceptWeekends()
    {
        ScheduleModifiers.ExceptWeekends(task);
        return this;
    }

    public IHourSchedulerBuilder ExceptCalendar(params string[] names)
    {
        ScheduleModifiers.ExceptCalendar(task, names);
        return this;
    }
}

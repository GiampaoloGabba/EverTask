namespace EverTask.Scheduler.Recurring.Builder;

public class IntervalSchedulerBuilder(RecurringTask task, TimeProvider? timeProvider) : IIntervalSchedulerBuilder
{
    /// <summary>The pre-P9 constructor, kept for binary compatibility (P6/X6).</summary>
    public IntervalSchedulerBuilder(RecurringTask task) : this(task, null) { }

    public IBuildableSchedulerBuilder UseCron(string cronExpression)
    {
        task.CronInterval = new CronInterval(cronExpression);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IEverySchedulerBuilder Every(int number) => new EverySchedulerBuilder(task, number, timeProvider);

    public IBuildableSchedulerBuilder EverySecond()
    {
        task.SecondInterval = new SecondInterval(1);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IHourSchedulerBuilder EveryHour()
    {
        task.HourInterval = new HourInterval(1);
        return new HourSchedulerBuilder(task, timeProvider);
    }

    public IMinuteSchedulerBuilder EveryMinute()
    {
        task.MinuteInterval = new MinuteInterval(1);
        return new MinuteSchedulerBuilder(task, timeProvider);
    }

    public IDailyTimeSchedulerBuilder EveryDay()
    {
        task.DayInterval = new DayInterval(1);
        return new DailyTimeSchedulerBuilder(task, timeProvider);
    }

    public IWeeklySchedulerBuilder EveryWeek()
    {
        task.WeekInterval = new WeekInterval(1);
        return new WeeklySchedulerBuilder(task, timeProvider);
    }

    public IMonthlySchedulerBuilder EveryMonth()
    {
        task.MonthInterval = new MonthInterval(1);
        return new MonthlySchedulerBuilder(task, timeProvider);
    }

    public IHourSchedulerBuilder OnHours()
    {
        task.HourInterval = new HourInterval(1);
        return new HourSchedulerBuilder(task, timeProvider);
    }

    public IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days)
    {
        task.DayInterval = new DayInterval(0, days);
        return new DailyTimeSchedulerBuilder(task, timeProvider);
    }

    public IMonthlySchedulerBuilder OnMonths(params int[] months)
    {
        task.MonthInterval = new MonthInterval(0, months);
        return new MonthlySchedulerBuilder(task, timeProvider);
    }

    // Zone-first chaining: Schedule().InTimeZone(z).EveryDay().AtTime(...). The interval methods below return
    // their own refining builder, so declaring InTimeZone here as well is what lets the call sit before the
    // interval instead of only after it.
    public IIntervalSchedulerBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        task.SetTimeZone(timeZone);
        return this;
    }

    public IIntervalSchedulerBuilder InTimeZone(string timeZoneId)
    {
        task.SetTimeZone(timeZoneId);
        return this;
    }

    public IIntervalSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure)
    {
        ScheduleModifiers.OnMisfire(task, configure);
        return this;
    }

    public IIntervalSchedulerBuilder WithDurableOccurrences()
    {
        ScheduleModifiers.WithDurableOccurrences(task);
        return this;
    }

    public IIntervalSchedulerBuilder BackfillFrom(DateTimeOffset startUtc)
    {
        ScheduleModifiers.BackfillFrom(task, startUtc);
        return this;
    }

    public IBuildableSchedulerBuilder UseOccurrenceProvider(string key, string? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        task.Provider = new ProviderSettings { Key = key, Config = config };

        // The BUILDABLE builder, not this one: a provider replaces the grid, so the interval methods that
        // follow it would be refused by Validate anyway (a provider and an interval are exclusive), and
        // ending the chain here is what says so at compile time instead.
        return new BuildableSchedulerBuilder(task, timeProvider);
    }
}

namespace EverTask.Scheduler.Recurring.Builder;

public class MonthlySchedulerBuilder(RecurringTask task, TimeProvider? timeProvider) : IMonthlySchedulerBuilder
{
    /// <summary>The pre-P9 constructor, kept for binary compatibility (P6/X6).</summary>
    public MonthlySchedulerBuilder(RecurringTask task) : this(task, null) { }

    public IDailyTimeSchedulerBuilder OnDay(int day)
    {
        ArgumentNullException.ThrowIfNull(task.MonthInterval);

        //check if day is valid in this month
        if (day is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(day));

        task.MonthInterval.OnDay = day;
        return new DailyTimeSchedulerBuilder(task, timeProvider);
    }

    public IDailyTimeSchedulerBuilder OnDays(params int[] days)
    {
        ArgumentNullException.ThrowIfNull(task.MonthInterval);
        task.MonthInterval.OnDays = days.Distinct().ToArray();
        return new DailyTimeSchedulerBuilder(task, timeProvider);
    }

    public IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day)
    {
        ArgumentNullException.ThrowIfNull(task.MonthInterval);

        task.MonthInterval.OnFirst = day;
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

    // Returns THIS builder: EveryMonth().InTimeZone(z).OnDay(15) has to keep the day selector reachable.
    public IMonthlySchedulerBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        task.SetTimeZone(timeZone);
        return this;
    }

    public IMonthlySchedulerBuilder InTimeZone(string timeZoneId)
    {
        task.SetTimeZone(timeZoneId);
        return this;
    }

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.InTimeZone(TimeZoneInfo timeZone) =>
        InTimeZone(timeZone);

    IBuildableSchedulerBuilder IBuildableSchedulerBuilder.InTimeZone(string timeZoneId) =>
        InTimeZone(timeZoneId);
}

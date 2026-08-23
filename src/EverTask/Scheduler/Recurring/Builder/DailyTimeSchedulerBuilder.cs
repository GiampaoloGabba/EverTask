namespace EverTask.Scheduler.Recurring.Builder;

public class DailyTimeSchedulerBuilder(RecurringTask task, TimeProvider? timeProvider)
    : IDailyTimeSchedulerBuilder
{
    /// <summary>
    /// The pre-P9 constructor, kept as a real overload so an assembly compiled against the previous release
    /// still binds (P6/X6). Every builder in this namespace keeps its original arity for the same reason.
    /// </summary>
    public DailyTimeSchedulerBuilder(RecurringTask task) : this(task, null) { }

    public IBuildableSchedulerBuilder AtTime(TimeOnly time)
    {
        if (task.DayInterval == null && task.WeekInterval == null && task.MonthInterval == null)
            throw new InvalidOperationException("DayInterval, WeekInterval, or MonthInterval must be set");

        if (task.DayInterval != null)
            task.DayInterval.OnTimes = [time.ToUniversalTime()];
        else if (task.WeekInterval != null)
            task.WeekInterval.OnTimes = [time.ToUniversalTime()];
        else if (task.MonthInterval != null)
            task.MonthInterval.OnTimes = [time.ToUniversalTime()];

        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder AtTimes(params TimeOnly[] times)
    {
        if (task.DayInterval == null && task.WeekInterval == null && task.MonthInterval == null)
            throw new InvalidOperationException("DayInterval, WeekInterval, or MonthInterval must be set");

        // Note: OnTimes property setter will sort the array automatically
        var utcTimes = times.Select(time => time.ToUniversalTime()).Distinct().ToArray();

        if (task.DayInterval != null)
            task.DayInterval.OnTimes = utcTimes;
        else if (task.WeekInterval != null)
            task.WeekInterval.OnTimes = utcTimes;
        else if (task.MonthInterval != null)
            task.MonthInterval.OnTimes = utcTimes;

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
}

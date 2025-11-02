namespace EverTask.Scheduler.Recurring.Builder;

public class DailyTimeSchedulerBuilder(RecurringTask task) : IDailyTimeSchedulerBuilder
{
    public IBuildableSchedulerBuilder AtTime(TimeOnly time)
    {
        if (task.DayInterval == null && task.WeekInterval == null && task.MonthInterval == null)
            throw new InvalidOperationException("DayInterval, WeekInterval, or MonthInterval must be set");

        if (task.DayInterval != null)
            task.DayInterval.OnTimes = new[] { time.ToUniversalTime() };
        else if (task.WeekInterval != null)
            task.WeekInterval.OnTimes = new[] { time.ToUniversalTime() };
        else if (task.MonthInterval != null)
            task.MonthInterval.OnTimes = new[] { time.ToUniversalTime() };

        return new BuildableSchedulerBuilder(task);
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

        return new BuildableSchedulerBuilder(task);
    }

    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset runUntil)
    {
        var runUntilUtc = runUntil.ToUniversalTime();
        if (runUntilUtc < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("RunUntil cannot be in the past");

        task.RunUntil = runUntilUtc;
        return new BuildableSchedulerBuilder(task);
    }

    public void MaxRuns(int maxRuns) => task.MaxRuns = maxRuns;
}

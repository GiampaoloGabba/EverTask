﻿namespace EverTask.Scheduler.Recurring.Builder;

public class DailyTimeSchedulerBuilder(RecurringTask task) : IDailyTimeSchedulerBuilder
{
    public IBuildableSchedulerBuilder AtTime(TimeOnly time)
    {
        if (task.DayInterval == null && task.MonthInterval == null)
            throw new InvalidOperationException("DayInterval or MonthInterval must be set");

        if (task.DayInterval != null)
            task.DayInterval.OnTimes = new[] { time.ToUniversalTime() };
        else if (task.MonthInterval != null)
            task.MonthInterval.OnTimes = new[] { time.ToUniversalTime() };

        return new BuildableSchedulerBuilder(task);
    }

    public IBuildableSchedulerBuilder AtTime(params TimeOnly[] times)
    {
        if (task.DayInterval == null && task.MonthInterval == null)
            throw new InvalidOperationException("DayInterval or MonthInterval must be set");

        var utcTimes = times.Select(time => time.ToUniversalTime()).Distinct().ToArray();

        if (task.DayInterval != null)
            task.DayInterval.OnTimes = utcTimes;
        else if (task.MonthInterval != null)
            task.MonthInterval.OnTimes = utcTimes;

        return new BuildableSchedulerBuilder(task);
    }

    public void MaxRuns(int maxRuns) => task.MaxRuns = maxRuns;
}

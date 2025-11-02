namespace EverTask.Scheduler.Recurring.Builder;

public class WeeklySchedulerBuilder(RecurringTask task) : IWeeklySchedulerBuilder
{
    public IDailyTimeSchedulerBuilder OnDay(DayOfWeek day)
    {
        if (task.WeekInterval == null)
            throw new InvalidOperationException("WeekInterval must be set before calling OnDay");

        task.WeekInterval.OnDays = [day];
        return new DailyTimeSchedulerBuilder(task);
    }

    public IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days)
    {
        if (task.WeekInterval == null)
            throw new InvalidOperationException("WeekInterval must be set before calling OnDays");

        task.WeekInterval.OnDays = days.Distinct().ToArray();
        return new DailyTimeSchedulerBuilder(task);
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

namespace EverTask.Scheduler.Recurring.Builder;

public class MonthlySchedulerBuilder(RecurringTask task) : IMonthlySchedulerBuilder
{
    public IDailyTimeSchedulerBuilder OnDay(int day)
    {
        ArgumentNullException.ThrowIfNull(task.MonthInterval);

        //check if day is valid in this month
        if (day is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(day));

        task.MonthInterval.OnDay = day;
        return new DailyTimeSchedulerBuilder(task);
    }

    public IDailyTimeSchedulerBuilder OnDays(params int[] days)
    {
        ArgumentNullException.ThrowIfNull(task.MonthInterval);
        task.MonthInterval.OnDays = days.Distinct().ToArray();
        return new DailyTimeSchedulerBuilder(task);
    }

    public IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day)
    {
        ArgumentNullException.ThrowIfNull(task.MonthInterval);

        task.MonthInterval.OnFirst = day;
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

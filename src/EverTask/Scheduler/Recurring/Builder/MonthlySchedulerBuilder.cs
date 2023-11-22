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

    public IDailyTimeSchedulerBuilder OnFirst(DayOfWeek day)
    {
        ArgumentNullException.ThrowIfNull(task.MonthInterval);

        task.MonthInterval.OnFirst = day;
        return new DailyTimeSchedulerBuilder(task);
    }

    public void MaxRuns(int maxRuns) => task.MaxRuns = maxRuns;
}

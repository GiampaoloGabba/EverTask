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
}

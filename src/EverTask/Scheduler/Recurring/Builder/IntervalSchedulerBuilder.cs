using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Scheduler.Recurring.Builder;

public class IntervalSchedulerBuilder(RecurringTask task) : IIntervalSchedulerBuilder
{
    public IBuildableSchedulerBuilder UseCron(string cronExpression)
    {
        task.CronInterval = new CronInterval(cronExpression);
        return new BuildableSchedulerBuilder(task);
    }

    public IEverySchedulerBuilder Every(int number) => new EverySchedulerBuilder(task, number);

    public IBuildableSchedulerBuilder EverySecond()
    {
        task.SecondInterval = new SecondInterval(1);
        return new BuildableSchedulerBuilder(task);
    }

    public IHourSchedulerBuilder EveryHour()
    {
        task.HourInterval = new HourInterval(1);
        return new HourSchedulerBuilder(task);
    }

    public IMinuteSchedulerBuilder EveryMinute()
    {
        task.MinuteInterval = new MinuteInterval(1);
        return new MinuteSchedulerBuilder(task);
    }

    public IDailyTimeSchedulerBuilder EveryDay()
    {
        task.DayInterval = new DayInterval(1);
        return new DailyTimeSchedulerBuilder(task);
    }

    public IMonthlySchedulerBuilder EveryMonth()
    {
        task.MonthInterval = new MonthInterval(1);
        return new MonthlySchedulerBuilder(task);
    }

    public IHourSchedulerBuilder OnHours()
    {
        task.HourInterval = new HourInterval(1);
        return new HourSchedulerBuilder(task);
    }

    public IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days)
    {
        task.DayInterval = new DayInterval(0, days);
        return new DailyTimeSchedulerBuilder(task);
    }

    public IMonthlySchedulerBuilder OnMonths(params int[] months)
    {
        task.MonthInterval = new MonthInterval(0, months);
        return new MonthlySchedulerBuilder(task);
    }
}

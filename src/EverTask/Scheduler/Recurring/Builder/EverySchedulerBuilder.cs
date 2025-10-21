namespace EverTask.Scheduler.Recurring.Builder;

public class EverySchedulerBuilder : IEverySchedulerBuilder
{
    private readonly RecurringTask _task;
    private readonly int _interval;

    public EverySchedulerBuilder(RecurringTask task, int interval)
    {
        if (interval <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval));

        _task     = task;
        _interval = interval;
    }

    public IBuildableSchedulerBuilder Seconds()
    {
        _task.SecondInterval = new SecondInterval(_interval);
        return new BuildableSchedulerBuilder(_task);
    }

    public IMinuteSchedulerBuilder Minutes()
    {
        _task.MinuteInterval = new MinuteInterval(_interval);
        return new MinuteSchedulerBuilder(_task);
    }

    public IHourSchedulerBuilder Hours()
    {
        _task.HourInterval = new HourInterval(_interval);
        return new HourSchedulerBuilder(_task);
    }

    public IDailyTimeSchedulerBuilder Days()
    {
        _task.DayInterval = new DayInterval(_interval);
        return new DailyTimeSchedulerBuilder(_task);
    }

    public IMonthlySchedulerBuilder Months()
    {
        _task.MonthInterval = new MonthInterval(_interval);
        return new MonthlySchedulerBuilder(_task);
    }
}

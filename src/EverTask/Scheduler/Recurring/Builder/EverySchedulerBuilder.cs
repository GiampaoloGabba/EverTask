namespace EverTask.Scheduler.Recurring.Builder;

public class EverySchedulerBuilder : IEverySchedulerBuilder
{
    private readonly RecurringTask _task;
    private readonly int _interval;
    private readonly TimeProvider? _timeProvider;

    /// <summary>The pre-P9 constructor, kept for binary compatibility (P6/X6).</summary>
    public EverySchedulerBuilder(RecurringTask task, int interval) : this(task, interval, null) { }

    public EverySchedulerBuilder(RecurringTask task, int interval, TimeProvider? timeProvider)
    {
        if (interval <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval));

        _task         = task;
        _interval     = interval;
        _timeProvider = timeProvider;
    }

    public IBuildableSchedulerBuilder Seconds()
    {
        _task.SecondInterval = new SecondInterval(_interval);
        return new BuildableSchedulerBuilder(_task, _timeProvider);
    }

    public IMinuteSchedulerBuilder Minutes()
    {
        _task.MinuteInterval = new MinuteInterval(_interval);
        return new MinuteSchedulerBuilder(_task, _timeProvider);
    }

    public IHourSchedulerBuilder Hours()
    {
        _task.HourInterval = new HourInterval(_interval);
        return new HourSchedulerBuilder(_task, _timeProvider);
    }

    public IDailyTimeSchedulerBuilder Days()
    {
        _task.DayInterval = new DayInterval(_interval);
        return new DailyTimeSchedulerBuilder(_task, _timeProvider);
    }

    public IWeeklySchedulerBuilder Weeks()
    {
        _task.WeekInterval = new WeekInterval(_interval);
        return new WeeklySchedulerBuilder(_task, _timeProvider);
    }

    public IMonthlySchedulerBuilder Months()
    {
        _task.MonthInterval = new MonthInterval(_interval);
        return new MonthlySchedulerBuilder(_task, _timeProvider);
    }
}

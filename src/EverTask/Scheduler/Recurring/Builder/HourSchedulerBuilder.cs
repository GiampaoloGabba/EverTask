namespace EverTask.Scheduler.Recurring.Builder;

public class HourSchedulerBuilder(RecurringTask task) : IHourSchedulerBuilder
{
    public IMinuteSchedulerBuilder AtMinute(int minute)
    {
        ArgumentNullException.ThrowIfNull(task.HourInterval);

        if (minute is < 0 or > 59)
            throw new ArgumentOutOfRangeException(nameof(minute));

        task.HourInterval.OnMinute = minute;
        return new MinuteSchedulerBuilder(task);
    }

    public void MaxRuns(int maxRuns) =>task.MaxRuns = maxRuns;
}

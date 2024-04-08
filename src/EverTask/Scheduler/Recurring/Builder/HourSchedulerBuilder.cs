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

    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset runUntil)
    {
        if (runUntil.ToUniversalTime() < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("RunUntil cannot be in the past");

        task.RunUntil = runUntil;
        return new BuildableSchedulerBuilder(task);
    }

    public void MaxRuns(int maxRuns) =>task.MaxRuns = maxRuns;
}

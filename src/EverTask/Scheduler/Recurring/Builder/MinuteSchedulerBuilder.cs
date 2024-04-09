namespace EverTask.Scheduler.Recurring.Builder;

public class MinuteSchedulerBuilder(RecurringTask task) : IMinuteSchedulerBuilder
{
    public IBuildableSchedulerBuilder AtSecond(int second)
    {
        if (task.MinuteInterval == null && task.HourInterval == null)
            throw new InvalidOperationException("MinuteInterval or HourInterval must be set");

        if (second is < 0 or > 59)
            throw new ArgumentOutOfRangeException(nameof(second));

        if (task.MinuteInterval != null)
            task.MinuteInterval.OnSecond = second;
        else if (task.HourInterval != null)
            task.HourInterval.OnSecond = second;

        return new BuildableSchedulerBuilder(task);
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

namespace EverTask.Scheduler.Recurring.Builder;

public class MinuteSchedulerBuilder(RecurringTask task, TimeProvider? timeProvider) : IMinuteSchedulerBuilder
{
    /// <summary>The pre-P9 constructor, kept for binary compatibility (P6/X6).</summary>
    public MinuteSchedulerBuilder(RecurringTask task) : this(task, null) { }

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

        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset runUntil)
    {
        var runUntilUtc = runUntil.ToUniversalTime();
        if (runUntilUtc < (timeProvider ?? TimeProvider.System).GetUtcNow())
            throw new InvalidOperationException("RunUntil cannot be in the past");

        task.RunUntil = runUntilUtc;
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public void MaxRuns(int maxRuns) => task.MaxRuns = maxRuns;

    public IMinuteSchedulerBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        task.SetTimeZone(timeZone);
        return this;
    }

    public IMinuteSchedulerBuilder InTimeZone(string timeZoneId)
    {
        task.SetTimeZone(timeZoneId);
        return this;
    }
}

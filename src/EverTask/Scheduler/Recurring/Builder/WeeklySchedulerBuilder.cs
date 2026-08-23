namespace EverTask.Scheduler.Recurring.Builder;

public class WeeklySchedulerBuilder(RecurringTask task, TimeProvider? timeProvider) : IWeeklySchedulerBuilder
{
    /// <summary>The pre-P9 constructor, kept for binary compatibility (P6/X6).</summary>
    public WeeklySchedulerBuilder(RecurringTask task) : this(task, null) { }

    public IDailyTimeSchedulerBuilder OnDay(DayOfWeek day)
    {
        if (task.WeekInterval == null)
            throw new InvalidOperationException("WeekInterval must be set before calling OnDay");

        task.WeekInterval.OnDays = [day];
        return new DailyTimeSchedulerBuilder(task, timeProvider);
    }

    public IDailyTimeSchedulerBuilder OnDays(params DayOfWeek[] days)
    {
        if (task.WeekInterval == null)
            throw new InvalidOperationException("WeekInterval must be set before calling OnDays");

        task.WeekInterval.OnDays = days.Distinct().ToArray();
        return new DailyTimeSchedulerBuilder(task, timeProvider);
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
}

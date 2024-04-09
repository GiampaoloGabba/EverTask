namespace EverTask.Scheduler.Recurring.Builder;

internal class RecurringTaskBuilder : IRecurringTaskBuilder
{
    internal readonly RecurringTask RecurringTask = new();

    public IThenableSchedulerBuilder RunNow()
    {
        RecurringTask.RunNow = true;
        return RunAt(DateTimeOffset.UtcNow);
    }

    public IThenableSchedulerBuilder RunDelayed(TimeSpan delay)
    {
        RecurringTask.InitialDelay = delay;
        return new ThenableSchedulerBuilder(RecurringTask);
    }

    public IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset)
    {
        RecurringTask.SpecificRunTime = dateTimeOffset;
        return new ThenableSchedulerBuilder(RecurringTask);
    }

    public IIntervalSchedulerBuilder Schedule() => new IntervalSchedulerBuilder(RecurringTask);
}

public class ThenableSchedulerBuilder(RecurringTask recurringTask) : IThenableSchedulerBuilder
{
    public IIntervalSchedulerBuilder Then() => new IntervalSchedulerBuilder(recurringTask);
}

public class BuildableSchedulerBuilder(RecurringTask task) : IBuildableSchedulerBuilder
{
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

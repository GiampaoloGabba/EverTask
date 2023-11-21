using Cronos;

namespace EverTask.Scheduler.Builder;

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

public class IntervalSchedulerBuilder(RecurringTask recurringTask) : IIntervalSchedulerBuilder
{
    public IBuildableSchedulerBuilder UseCron(string cronExpression)
    {
        recurringTask.CronExpression = cronExpression;
        return new BuildableSchedulerBuilder(recurringTask);
    }
}

public class BuildableSchedulerBuilder(RecurringTask recurringTask) : IBuildableSchedulerBuilder
{
    public void MaxRuns(int maxRuns)
    {
        recurringTask.MaxRuns = maxRuns;
    }
}

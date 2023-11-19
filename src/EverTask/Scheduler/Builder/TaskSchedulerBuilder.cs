using Cronos;

namespace EverTask.Scheduler.Builder;

internal class TaskSchedulerBuilder : ITaskSchedulerBuilder
{
    internal readonly ScheduledTask ScheduledTask = new();

    public IThenableSchedulerBuilder RunNow() => RunAt(DateTimeOffset.UtcNow);

    public IThenableSchedulerBuilder RunDelayed(TimeSpan delay) => RunAt(DateTimeOffset.UtcNow.Add(delay));

    public IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset)
    {
        ScheduledTask.SpecificRunTime = dateTimeOffset;
        return new ThenableSchedulerBuilder(ScheduledTask);
    }

    public IIntervalSchedulerBuilder Schedule() => new IntervalSchedulerBuilder(ScheduledTask);
}

public class ThenableSchedulerBuilder(ScheduledTask scheduledTask) : IThenableSchedulerBuilder
{
    public IIntervalSchedulerBuilder Then() => new IntervalSchedulerBuilder(scheduledTask);
}

public class IntervalSchedulerBuilder(ScheduledTask scheduledTask) : IIntervalSchedulerBuilder
{
    public IBuildableSchedulerBuilder UseCron(string cronExpression)
    {
        scheduledTask.CronExpression = cronExpression;
        return new BuildableSchedulerBuilder(scheduledTask);
    }
}

public class BuildableSchedulerBuilder(ScheduledTask scheduledTask) : IBuildableSchedulerBuilder
{
    public void MaxRuns(int maxRuns)
    {
        scheduledTask.MaxRuns = maxRuns;
    }
}

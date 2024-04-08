﻿namespace EverTask.Scheduler.Recurring.Builder;

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

public class BuildableSchedulerBuilder(RecurringTask recurringTask) : IBuildableSchedulerBuilder
{
    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset runUntil)
    {
        if (runUntil.ToUniversalTime() < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("RunUntil cannot be in the past");

        recurringTask.RunUntil = runUntil;
        return new BuildableSchedulerBuilder(recurringTask);
    }

    public void MaxRuns(int maxRuns) => recurringTask.MaxRuns = maxRuns;
}

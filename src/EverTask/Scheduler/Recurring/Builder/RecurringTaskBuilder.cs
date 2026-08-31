namespace EverTask.Scheduler.Recurring.Builder;

// The scheduling clock travels down the whole chain: RunNow and every RunUntil guard resolve on it,
// so a schedule built under a test clock is judged by that clock and not by the wall clock.
internal class RecurringTaskBuilder(TimeProvider? timeProvider = null) : IRecurringTaskBuilder
{
    internal readonly RecurringTask RecurringTask = new();

    public IThenableSchedulerBuilder RunNow()
    {
        RecurringTask.RunNow = true;
        return RunAt((timeProvider ?? TimeProvider.System).GetUtcNow());
    }

    public IThenableSchedulerBuilder RunDelayed(TimeSpan delay)
    {
        RecurringTask.InitialDelay = delay;
        return new ThenableSchedulerBuilder(RecurringTask, timeProvider);
    }

    public IThenableSchedulerBuilder RunAt(DateTimeOffset dateTimeOffset)
    {
        RecurringTask.SpecificRunTime = dateTimeOffset;
        return new ThenableSchedulerBuilder(RecurringTask, timeProvider);
    }

    public IIntervalSchedulerBuilder Schedule() => new IntervalSchedulerBuilder(RecurringTask, timeProvider);
}

public class ThenableSchedulerBuilder(RecurringTask recurringTask, TimeProvider? timeProvider)
    : IThenableSchedulerBuilder
{
    /// <summary>The historical constructor, kept so an assembly compiled against an earlier release binds.</summary>
    public ThenableSchedulerBuilder(RecurringTask recurringTask) : this(recurringTask, null) { }

    public IIntervalSchedulerBuilder Then() => new IntervalSchedulerBuilder(recurringTask, timeProvider);
}

public class BuildableSchedulerBuilder(RecurringTask task, TimeProvider? timeProvider) : IBuildableSchedulerBuilder
{
    /// <summary>The historical constructor, kept so an assembly compiled against an earlier release binds.</summary>
    public BuildableSchedulerBuilder(RecurringTask task) : this(task, null) { }

    public IBuildableSchedulerBuilder RunUntil(DateTimeOffset runUntil)
    {
        var runUntilUtc = runUntil.ToUniversalTime();
        if (runUntilUtc < (timeProvider ?? TimeProvider.System).GetUtcNow())
            throw new InvalidOperationException("RunUntil cannot be in the past");

        task.RunUntil = runUntilUtc;
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public void MaxRuns(int maxRuns) => task.MaxRuns = maxRuns;

    public IBuildableSchedulerBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        task.SetTimeZone(timeZone);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder InTimeZone(string timeZoneId)
    {
        task.SetTimeZone(timeZoneId);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder OnMisfire(Action<IMisfirePolicyBuilder> configure)
    {
        ScheduleModifiers.OnMisfire(task, configure);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder WithDurableOccurrences()
    {
        ScheduleModifiers.WithDurableOccurrences(task);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder BackfillFrom(DateTimeOffset startUtc)
    {
        ScheduleModifiers.BackfillFrom(task, startUtc);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder Except(Action<IExclusionBuilder> configure)
    {
        ScheduleModifiers.Except(task, configure);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder ExceptWeekends()
    {
        ScheduleModifiers.ExceptWeekends(task);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }

    public IBuildableSchedulerBuilder ExceptCalendar(params string[] names)
    {
        ScheduleModifiers.ExceptCalendar(task, names);
        return new BuildableSchedulerBuilder(task, timeProvider);
    }
}

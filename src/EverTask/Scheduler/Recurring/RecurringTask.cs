namespace EverTask.Scheduler.Recurring;

public class RecurringTask
{
    public bool            RunNow          { get; set; }
    public TimeSpan?       InitialDelay    { get; set; }
    public DateTimeOffset? SpecificRunTime { get; set; }
    public CronInterval?   CronInterval    { get; set; }
    public SecondInterval? SecondInterval  { get; set; }
    public MinuteInterval? MinuteInterval  { get; set; }
    public HourInterval?   HourInterval    { get; set; }
    public DayInterval?    DayInterval     { get; set; }
    public MonthInterval?  MonthInterval   { get; set; }
    public int?            MaxRuns         { get; set; }
    public DateTimeOffset? RunUntil        { get; set; }

    //used for serialization/deserialization
    public RecurringTask() { }


    public DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun)
    {
        if (currentRun >= MaxRuns) return null;

        current = current.ToUniversalTime();

        if (RunUntil <= current) return null;

        DateTimeOffset? runtime = null;

        // For first run (currentRun == 0), check if we have initial run configuration
        if (currentRun == 0)
        {
            if (RunNow)
            {
                runtime = DateTimeOffset.UtcNow;
            }
            else if (SpecificRunTime.HasValue)
            {
                runtime = SpecificRunTime.Value.ToUniversalTime();
            }
            else if (InitialDelay.HasValue)
            {
                // InitialDelay always takes precedence - it defines the absolute first run time
                return current.Add(InitialDelay.Value);
            }
        }

        // Calculate next occurrence from the appropriate base time:
        // - If SpecificRunTime is set and in the past, calculate from SpecificRunTime to properly skip past occurrences
        // - Otherwise, calculate from current time
        var baseTime = (currentRun == 0 && runtime.HasValue && runtime.Value < current)
            ? runtime.Value
            : current;
        var next = GetNextOccurrence(baseTime);

        if (currentRun > 0) return next;

        if (next == null) return runtime;

        // For RunNow or SpecificRunTime, use runtime if:
        // 1. It's in the future (always use future SpecificRunTime)
        // 2. It's in the recent past (within 20 seconds) AND before next interval
        // No arbitrary gap required - the user explicitly requested this runtime
        if (runtime.HasValue)
        {
            // If runtime is in the future, always use it
            if (runtime.Value > current)
            {
                return runtime;
            }

            // If runtime is in the recent past, use it only if it's before next interval
            bool runtimeIsBeforeNext = runtime < next;
            bool notTooFarInPast = runtime.Value > current.AddSeconds(-20);

            if (runtimeIsBeforeNext && notTooFarInPast)
            {
                return runtime;
            }
        }

        return next;
    }

    /// <summary>
    /// Calculates the minimum interval for this recurring task.
    /// For cron expressions, calculates the interval between the next two occurrences.
    /// For interval-based tasks, returns the configured interval.
    /// </summary>
    /// <returns>Minimum interval between executions</returns>
    public TimeSpan GetMinimumInterval()
    {
        // Cron: calculate interval between next two occurrences
        if (CronInterval != null && !string.IsNullOrEmpty(CronInterval.CronExpression))
        {
            var now = DateTimeOffset.UtcNow;
            var first = CronInterval.GetNextOccurrence(now);
            if (!first.HasValue)
            {
                return TimeSpan.FromHours(1); // Fallback conservative
            }

            var second = CronInterval.GetNextOccurrence(first.Value);
            if (!second.HasValue)
            {
                return TimeSpan.FromHours(1); // Fallback conservative
            }

            var interval = second.Value - first.Value;
            return interval;
        }

        // Interval fields: use the most granular interval
        if (SecondInterval?.Interval > 0)
        {
            return TimeSpan.FromSeconds(SecondInterval.Interval);
        }
        if (MinuteInterval?.Interval > 0)
        {
            return TimeSpan.FromMinutes(MinuteInterval.Interval);
        }
        if (HourInterval?.Interval > 0)
        {
            return TimeSpan.FromHours(HourInterval.Interval);
        }
        if (DayInterval?.Interval > 0)
        {
            return TimeSpan.FromDays(DayInterval.Interval);
        }
        if (MonthInterval?.Interval > 0)
        {
            return TimeSpan.FromDays(30); // Conservative approximation
        }

        return TimeSpan.FromMinutes(5); // Safe default
    }

    private DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        if (!string.IsNullOrEmpty(CronInterval?.CronExpression))
        {
            var nextCron = CronInterval.GetNextOccurrence(current);
            if (nextCron == null || RunUntil <= nextCron)
                return null;

            return nextCron;
        }

        var nextRun = MonthInterval?.GetNextOccurrence(current) ?? current;
        nextRun = DayInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = HourInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = MinuteInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = SecondInterval?.GetNextOccurrence(nextRun) ?? nextRun;

        if (nextRun < current.AddSeconds(1) || nextRun >= RunUntil)
            return null;

        return nextRun;
    }

    #region ToString in human readable format

    public override string ToString()
    {
        var parts = new List<string>();

        if (RunNow)
            parts.Add("Run immediately");

        if (InitialDelay != null)
            parts.Add($"Start after a delay of {InitialDelay.Value}");

        if (SpecificRunTime.HasValue)
            parts.Add($"Run at {SpecificRunTime.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        if (parts.Any())
            parts.Add("then");

        if (CronInterval != null)
        {
            parts.Add("Use Cron expression:");
            parts.Add(CronInterval.CronExpression);
            return string.Join(" ", parts);
        }

        if (SecondInterval is { Interval: > 0 }) parts.Add($"every {SecondInterval.Interval} second(s)");

        if (MinuteInterval != null)
        {
            if (MinuteInterval.Interval > 0)
                parts.Add($"every {MinuteInterval.Interval} minute(s)");
            if (MinuteInterval.OnSecond != 0)
                parts.Add($"at second {MinuteInterval.OnSecond}");
        }

        if (HourInterval != null)
        {
            if (HourInterval.Interval > 0)
                parts.Add($"every {HourInterval.Interval} hour(s)");
            if (HourInterval.OnHours.Any())
                parts.Add($"at hour(s) {string.Join(" - ", HourInterval.OnHours)}");
            if (HourInterval.OnMinute != null)
                parts.Add($"at minute {HourInterval.OnMinute}");
            if (HourInterval.OnSecond != null)
                parts.Add($"at second {HourInterval.OnSecond}");
        }

        if (DayInterval != null)
        {
            if (DayInterval.Interval > 0)
                parts.Add($"every {DayInterval.Interval} day(s)");
            if (DayInterval.OnTimes.Any())
                parts.Add($"at {string.Join(" - ", DayInterval.OnTimes)}");
            if (DayInterval.OnDays.Any())
                parts.Add($"on {string.Join(" - ", DayInterval.OnDays)}");
        }

        if (MonthInterval != null)
        {
            if (MonthInterval.Interval > 0)
                parts.Add($"every {MonthInterval.Interval} month(s)");
            if (MonthInterval.OnDay != null)
                parts.Add($"on day {MonthInterval.OnDay}");
            if (MonthInterval.OnFirst != null)
                parts.Add($"on first {MonthInterval.OnFirst}");
            if (MonthInterval.OnDays.Any())
                parts.Add($"on {string.Join(" - ", MonthInterval.OnDays)}");
            if (MonthInterval.OnTimes.Any())
                parts.Add($"at {string.Join(" - ", MonthInterval.OnTimes)}");
            if (MonthInterval.OnMonths.Any())
                parts.Add($"in {string.Join(" - ", MonthInterval.OnMonths)}");
        }

        if (RunUntil != null)
            parts.Add($"until {RunUntil.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        if (MaxRuns != null)
            parts.Add($"up to {MaxRuns} times");

        return string.Join(" ", parts);
    }

    #endregion
}

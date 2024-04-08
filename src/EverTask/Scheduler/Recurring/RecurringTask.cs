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

        if (RunUntil!=null && RunUntil >= DateTimeOffset.UtcNow) return null;

        var next = GetNextOccurrence(current);

        DateTimeOffset? runtime = null;

        if (currentRun > 0) return next;

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
            runtime = current.Add(InitialDelay.Value);
        }

        if (next == null) return runtime;

        // Return runtime only if it's before next and also before the current time, but not too much before...
        // and there is at least a 30 seconds gap between runtime and next.
        // This prevents closely spaced executions in case of delays or missed runs.
        if (runtime < next && runtime < current.AddSeconds(1) && runtime > current.AddSeconds(-20) &&
            (next.Value - runtime.Value).TotalSeconds >= 30)
            return runtime;

        return next;
    }

    private DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        if (!string.IsNullOrEmpty(CronInterval?.CronExpression))
        {
            var nextCron = CronInterval.GetNextOccurrence(current);
            if (nextCron == null || nextCron > RunUntil)
                return null;

            return nextCron;
        }

        var nextRun = MonthInterval?.GetNextOccurrence(current) ?? current;
        nextRun = DayInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = HourInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = MinuteInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = SecondInterval?.GetNextOccurrence(nextRun) ?? nextRun;

        if (nextRun < current.AddSeconds(1) || nextRun > RunUntil)
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

namespace EverTask.Scheduler.Builder;

public class ScheduledTask
{
    public TimeZoneInfo?   TimeZone         { get; set; }
    public bool            FirstRun         { get; set; }
    public bool            FirstRunExecuted { get; private set; }
    public TimeSpan?       InitialDelay     { get; set; }
    public DateTimeOffset? SpecificRunTime  { get; set; }

    public MinuteInterval? MinuteInterval { get; set; }
    public DayInterval?    DayInterval    { get; set; }
    public MonthInterval?  MonthInterval  { get; set; }

    public void SetFirstrunExecuted()
    {
        FirstRunExecuted = true;
    }

    public DateTimeOffset CalculateNextRun(DateTimeOffset current)
    {
        throw new NotImplementedException("Not implemented");
    }

    private DateTimeOffset? CalculateNextRunForRule(DateTimeOffset current)
    {
        throw new NotImplementedException("Not implemented");
        /*DateTimeOffset? nextTime = null;
        return nextTime;*/
    }

    public override string ToString()
    {
        var parts = new List<string>();

        var timeZoneOffset = TimeZone?.DisplayName ?? TimeZoneInfo.Local.DisplayName;
        parts.Add($"Time Zone: {timeZoneOffset}. ");

        if (InitialDelay == null && SpecificRunTime == null)
            parts.Add("Run immediately");

        if (InitialDelay.HasValue)
            parts.Add($"Start after a delay of {InitialDelay.Value}");

        if (SpecificRunTime.HasValue)
            parts.Add($"Run at {SpecificRunTime:yyyy-MM-dd HH:mm:ss}");

        if (parts.Any())
            parts.Add("then");

        if (MinuteInterval != null)
        {
            parts.Add($"every {MinuteInterval.Interval} minute(s)");
            if (MinuteInterval.OnSecond != 0)
                parts.Add($"at second {MinuteInterval.OnSecond}");
        }

        if (DayInterval != null)
        {
            parts.Add($"every {DayInterval.Interval} day(s)");
            if (DayInterval.OnTimes.Any())
                parts.Add($"at {string.Join(" - ", DayInterval.OnTimes)}");
            if (DayInterval.OnDays.Any())
                parts.Add($"on {string.Join(" - ", DayInterval.OnDays)}");
        }

        if (MonthInterval != null)
        {
            parts.Add($"every {MonthInterval.Interval} month(s)");
            if (MonthInterval.OnDay != null)
                parts.Add($"on day {MonthInterval.OnDay}");
            if (MonthInterval.OnFirst != null)
                parts.Add($"on first {MonthInterval.OnFirst}");
            if (MonthInterval.OnTimes.Any())
                parts.Add($"at {string.Join(" - ", MonthInterval.OnTimes)}");
            if (MonthInterval.OnMonths.Any())
                parts.Add($"in {string.Join(" - ", MonthInterval.OnMonths)}");
        }

        return string.Join(" ", parts);
    }
}

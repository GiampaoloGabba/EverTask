using Cronos;

namespace EverTask.Scheduler.Builder;

public class RecurringTask
{
    public TimeSpan?       InitialDelay    { get; set; }
    public DateTimeOffset? SpecificRunTime { get; set; }
    public MinuteInterval? MinuteInterval  { get; set; }
    public DayInterval?    DayInterval     { get; set; }
    public MonthInterval?  MonthInterval   { get; set; }
    public int             MaxRuns         { get; set; }

    //saving cronexp as string to easy serialization/deserialization
    public string?         CronExpression  { get; set; }

    //used to serialization/deserialization
    public RecurringTask() { }

    public DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun)
    {
        if (currentRun >= MaxRuns)
            return null;

        if (currentRun == 0)
        {
            var runtime = SpecificRunTime;
            if (runtime == null && InitialDelay != null)
                runtime = current.Add(InitialDelay.Value);

            if (runtime != null && runtime.Value.ToUniversalTime() <= current.ToUniversalTime().AddSeconds(1))
                return runtime.Value;
        }

        return CalculateNextSchedule(current);
    }

    private DateTimeOffset? CalculateNextSchedule(DateTimeOffset current)
    {
        var cronParsed = GetCronExpParsed();
        return cronParsed?.GetNextOccurrence(current, TimeZoneInfo.Utc)?.ToUniversalTime();
    }

    private CronExpression? GetCronExpParsed()
    {
        if (CronExpression == null)
            return null;

        var fields = CronExpression.Split(' ');

        return fields.Length switch
        {
            6 => Cronos.CronExpression.Parse(CronExpression, CronFormat.IncludeSeconds),
            5 => Cronos.CronExpression.Parse(CronExpression, CronFormat.Standard),
            _ => throw new ArgumentException("Invalid Cron Expression", nameof(CronExpression))
        };
    }

    #region ToString in human readable format
    public override string ToString()
    {
        var parts = new List<string>();

        if (InitialDelay == null && SpecificRunTime == null)
            parts.Add("Run immediately");

        if (InitialDelay != null)
            parts.Add($"Start after a delay of {InitialDelay.Value}");

        if (SpecificRunTime.HasValue)
            parts.Add($"Run at {SpecificRunTime:yyyy-MM-dd HH:mm:ss}");

        if (parts.Any())
            parts.Add("then");

        if (CronExpression != null)
        {
            parts.Add("Use Cron expression:");
            parts.Add(CronExpression.ToString());
            return string.Join(" ", parts);
        }

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
    #endregion
}

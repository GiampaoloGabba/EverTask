using Cronos;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Scheduler.Recurring;

public class RecurringTask
{
    public bool            RunNow          { get; set; }
    public TimeSpan?       InitialDelay    { get; set; }
    public DateTimeOffset? SpecificRunTime { get; set; }
    public SecondInterval? SecondInterval  { get; set; }
    public MinuteInterval? MinuteInterval  { get; set; }
    public HourInterval?   HourInterval    { get; set; }
    public DayInterval?    DayInterval     { get; set; }
    public MonthInterval?  MonthInterval   { get; set; }
    public int             MaxRuns         { get; set; } = 1;

    //saving cronexp as string to easy serialization/deserialization
    public string? CronExpression { get; set; }

    //used to serialization/deserialization
    public RecurringTask() { }

    public DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun)
    {
        if (currentRun >= MaxRuns) return null;

        current = current.ToUniversalTime();

        if (currentRun == 0)
        {
            DateTimeOffset? runtime = null;

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

            if (runtime.HasValue && runtime.Value <= current.AddSeconds(1))
            {
                return runtime.Value;
            }
        }

        return CalculateNextSchedule(current);
    }

    private DateTimeOffset? CalculateNextSchedule(DateTimeOffset current)
    {
        if (!string.IsNullOrEmpty(CronExpression))
            return GetNextCronOccurrence(current);

        var nextRun = GetNextMonthOccurrence(current);
        nextRun     = GetNextDayOccurrence(nextRun);
        nextRun     = GetNextHourOccurrence(nextRun);
        nextRun     = GetNextMinuteOccurrence(nextRun);
        return GetNextSecondOccurrence(nextRun);
    }

    private DateTimeOffset? GetNextCronOccurrence(DateTimeOffset current)
    {
        if (CronExpression == null) return null;

        var fields = CronExpression.Split(' ');

        var cronParsed = fields.Length switch
        {
            6 => Cronos.CronExpression.Parse(CronExpression, CronFormat.IncludeSeconds),
            5 => Cronos.CronExpression.Parse(CronExpression, CronFormat.Standard),
            _ => throw new ArgumentException("Invalid Cron Expression", nameof(CronExpression))
        };

        return cronParsed?.GetNextOccurrence(current, TimeZoneInfo.Utc)?.ToUniversalTime();
    }

    private DateTimeOffset GetNextMonthOccurrence(DateTimeOffset current)
    {
        if (MonthInterval == null) return current;

        if (MonthInterval.Interval == 0 && !MonthInterval.OnMonths.Any())
            throw new ArgumentException("Invalid Month Interval, you must specify at least one month.",
                nameof(DayInterval));

        var nextMonth = current.AddMonths(MonthInterval.Interval);

        if (MonthInterval.OnMonths.Any())
            nextMonth = nextMonth.NextValidMonth(MonthInterval.OnMonths);

        nextMonth = MonthInterval.OnFirst != null
                        ? nextMonth.FindFirstOccurrenceOfDayOfWeekInMonth(MonthInterval.OnFirst.Value)
                        : nextMonth.AdjustDayToValidMonthDay(MonthInterval.OnDay)
                                   .NextValidMonth(MonthInterval.OnMonths);

        return nextMonth.GetNextRequestedTime(current, MonthInterval.OnTimes, false);
    }

    private DateTimeOffset GetNextDayOccurrence(DateTimeOffset current)
    {
        if (DayInterval == null) return current;

        if (DayInterval.Interval == 0 && !DayInterval.OnDays.Any())
            throw new ArgumentException("Invalid Day Interval, you must specify at least one day.",
                nameof(DayInterval));

        var nextDay = current.AddDays(DayInterval.Interval);

        if (DayInterval.OnDays.Any())
            nextDay = nextDay.NextValidDayOfWeek(DayInterval.OnDays);

        return nextDay.GetNextRequestedTime(current, DayInterval.OnTimes);
    }

    private DateTimeOffset GetNextHourOccurrence(DateTimeOffset current)
    {
        if (HourInterval == null) return current;

        var nextHour = current.AddHours(HourInterval.Interval);
        return nextHour.Adjust(minute: HourInterval.OnMinute, second: HourInterval.OnSecond);
    }

    private DateTimeOffset GetNextMinuteOccurrence(DateTimeOffset current)
    {
        if (MinuteInterval == null) return current;

        var next = current.AddMinutes(MinuteInterval.Interval);
        return MinuteInterval.OnSecond != 0 ? next.Adjust(second: MinuteInterval.OnSecond) : next;
    }

    private DateTimeOffset GetNextSecondOccurrence(DateTimeOffset current) =>
        SecondInterval == null ? current : current.AddSeconds(SecondInterval.Interval);

    #region ToString in human readable format

    public override string ToString()
    {
        var parts = new List<string>();

        if (RunNow)
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
            parts.Add(CronExpression);
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

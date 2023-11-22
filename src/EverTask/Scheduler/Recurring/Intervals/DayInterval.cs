namespace EverTask.Scheduler.Recurring.Intervals;

public class DayInterval : IInterval
{
    //used for serialization/deserialization
    public DayInterval() { }

    public DayInterval(int interval)
    {
        Interval = interval;
    }

    public DayInterval(int interval, DayOfWeek[] onDays)
    {
        Interval = interval;
        OnDays   = onDays.Distinct().ToArray();
    }

    public int         Interval { get;  } = 1;
    public TimeOnly[]  OnTimes  { get; set; } = [TimeOnly.Parse("00:00")];
    public DayOfWeek[] OnDays   { get; } = Array.Empty<DayOfWeek>();

    public void Validate()
    {
        if (Interval == 0 && !OnDays.Any())
            throw new ArgumentException("Invalid Day Interval, you must specify at least one day.",
                nameof(DayInterval));
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        Validate();

        var nextDay = current.AddDays(Interval);

        if (OnDays.Any())
            nextDay = nextDay.NextValidDayOfWeek(OnDays);

        return nextDay.GetNextRequestedTime(current, OnTimes);
    }
}

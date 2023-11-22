namespace EverTask.Scheduler.Recurring.Intervals;

public class DayInterval
{
    //used to serialization/deserialization
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

    public int         Interval { get; set; } = 1;
    public TimeOnly[]  OnTimes  { get; set; } = [TimeOnly.Parse("00:00")];
    public DayOfWeek[] OnDays   { get; set; } = Array.Empty<DayOfWeek>();
}

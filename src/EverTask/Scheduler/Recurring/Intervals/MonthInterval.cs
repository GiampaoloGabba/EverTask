namespace EverTask.Scheduler.Recurring.Intervals;

public class MonthInterval
{
    //used to serialization/deserialization
    public MonthInterval() { }

    public MonthInterval(int interval)
    {
        Interval = interval;
    }

    public MonthInterval(int interval, int[] onMonths)
    {
        Interval = interval;
        OnMonths = onMonths.Distinct().ToArray();
    }

    public int        Interval { get; set; }
    public int?       OnDay    { get; set; }
    public DayOfWeek? OnFirst  { get; set; }
    public TimeOnly[] OnTimes  { get; set; } = [TimeOnly.Parse("00:00")];
    public int[]      OnMonths { get; set; } = [];
}

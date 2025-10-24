namespace EverTask.Scheduler.Recurring.Intervals;

public class HourInterval : IInterval
{
    //used for serialization/deserialization
    [JsonConstructor]
    public HourInterval() { }

    public HourInterval(int interval)
    {
        Interval = interval;
    }

    public HourInterval(int interval, int[] onHours)
    {
        Interval = interval;
        OnHours = onHours.Distinct().ToArray();
    }

    public int Interval { get; init; }
    public int? OnMinute { get; set; }
    public int? OnSecond { get; set; }
    public int[] OnHours { get; set; } = Array.Empty<int>();


    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        var next = current.AddHours(Interval);

        if (OnHours.Any())
            next = next.NextValidHour(OnHours);

        if (OnMinute != null)
            next = next.Adjust(minute: OnMinute);

        if (OnSecond != null)
            next = next.Adjust(second: OnSecond);

        return next;
    }
}

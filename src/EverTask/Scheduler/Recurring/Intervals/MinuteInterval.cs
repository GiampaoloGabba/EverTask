namespace EverTask.Scheduler.Recurring.Intervals;

public class MinuteInterval : IInterval
{
    //used for serialization/deserialization
    [System.Text.Json.Serialization.JsonConstructor]
    public MinuteInterval() { }

    public MinuteInterval(int interval)
    {
        Interval = interval;
    }
    public int Interval { get; init; }
    public int OnSecond { get; set; }

    public void Validate()
    {
        if (Interval < 0)
            throw new ArgumentException("Invalid Minute Interval, the interval cannot be negative.",
                nameof(MinuteInterval));

        if (OnSecond is < 0 or > 59)
            throw new ArgumentException($"Invalid Minute Interval, OnSecond '{OnSecond}' is not a valid second (0-59).",
                nameof(MinuteInterval));
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        var next = current.AddMinutes(Interval);
        return OnSecond != 0 ? next.Adjust(second: OnSecond) : next;
    }
}

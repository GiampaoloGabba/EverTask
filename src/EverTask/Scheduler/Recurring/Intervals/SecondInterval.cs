namespace EverTask.Scheduler.Recurring.Intervals;

public class SecondInterval : IInterval
{
    //used for serialization/deserialization
    [JsonConstructor]
    public SecondInterval() { }

    public SecondInterval(int interval)
    {
        Interval = interval;
    }
    public int Interval { get; init; }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current) => current.AddSeconds(Interval);
}

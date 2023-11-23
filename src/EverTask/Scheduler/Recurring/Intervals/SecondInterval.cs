namespace EverTask.Scheduler.Recurring.Intervals;

public class SecondInterval : IInterval
{
    //used for serialization/deserialization
    public SecondInterval() { }

    public SecondInterval(int interval)
    {
        Interval = interval;
    }
    public int Interval { get; }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current) => current.AddSeconds(Interval);
}

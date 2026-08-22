using System.Text.Json.Serialization;

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

    public void Validate()
    {
        if (Interval < 0)
            throw new ArgumentException("Invalid Second Interval, the interval cannot be negative.",
                nameof(SecondInterval));
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current) => current.AddSeconds(Interval);
}

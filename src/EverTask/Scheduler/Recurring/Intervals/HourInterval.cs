namespace EverTask.Scheduler.Recurring.Intervals;

public class HourInterval : IInterval
{
    //used for serialization/deserialization
    [System.Text.Json.Serialization.JsonConstructor]
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

    public void Validate()
    {
        if (Interval < 0)
            throw new ArgumentException("Invalid Hour Interval, the interval cannot be negative.",
                nameof(HourInterval));

        // Out-of-range hour/minute/second selectors deserialize but throw downstream at NextValidHour/Adjust —
        // validate them here so recovery poisons the row cleanly (B2/gap #2).
        foreach (var hour in OnHours)
            if (hour < 0 || hour > 23)
                throw new ArgumentException($"Invalid Hour Interval, '{hour}' is not a valid hour (0-23).",
                    nameof(HourInterval));

        if (OnMinute is < 0 or > 59)
            throw new ArgumentException($"Invalid Hour Interval, OnMinute '{OnMinute}' is not a valid minute (0-59).",
                nameof(HourInterval));

        if (OnSecond is < 0 or > 59)
            throw new ArgumentException($"Invalid Hour Interval, OnSecond '{OnSecond}' is not a valid second (0-59).",
                nameof(HourInterval));
    }


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

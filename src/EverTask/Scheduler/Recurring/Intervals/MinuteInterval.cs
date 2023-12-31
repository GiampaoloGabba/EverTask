﻿namespace EverTask.Scheduler.Recurring.Intervals;

public class MinuteInterval : IInterval
{
    //used for serialization/deserialization
    public MinuteInterval() { }

    public MinuteInterval(int interval)
    {
        Interval = interval;
    }
    public int Interval { get; }
    public int OnSecond { get; set; }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        var next = current.AddMinutes(Interval);
        return OnSecond != 0 ? next.Adjust(second: OnSecond) : next;
    }
}

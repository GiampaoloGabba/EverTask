namespace EverTask.Scheduler.Recurring.Intervals;

public class HourInterval
{
    //used to serialization/deserialization
    public HourInterval() { }

    public HourInterval(int interval)
    {
        Interval = interval;
    }

    public int Interval { get; set; }
    public int OnMinute { get; set; }
    public int OnSecond { get; set; }
}

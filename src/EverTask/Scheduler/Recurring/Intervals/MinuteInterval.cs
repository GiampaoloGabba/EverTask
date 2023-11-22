namespace EverTask.Scheduler.Recurring.Intervals;

public class MinuteInterval
{
    //used to serialization/deserialization
    public MinuteInterval() { }

    public MinuteInterval(int interval)
    {
        Interval = interval;
    }
    public int Interval { get; set; }
    public int OnSecond { get; set; }
}

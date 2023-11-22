namespace EverTask.Scheduler.Recurring.Intervals;

public class SecondInterval
{
    //used to serialization/deserialization
    public SecondInterval() { }

    public SecondInterval(int interval)
    {
        Interval = interval;
    }
    public int Interval { get; set; }
}

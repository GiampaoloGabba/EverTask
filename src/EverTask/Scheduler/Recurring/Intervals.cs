namespace EverTask.Scheduler.Builder;

public class MinuteInterval
{
    //used to serialization/deserialization
    public MinuteInterval() { }
    public int Interval { get; set; }
    public int OnSecond { get; set; }

    public static MinuteInterval Create(int interval, int? onSecond)
    {
        if (interval == 0) interval = 1;

        return new MinuteInterval
        {
            Interval = interval,
            OnSecond = onSecond ?? 0
        };
    }
}

public class DayInterval
{
    //used to serialization/deserialization
    public DayInterval() { }

    public int        Interval { get; set; } = 1;
    public TimeOnly[] OnTimes  { get; set; } = [];
    public Day[]      OnDays   { get; set; } = Array.Empty<Day>();

    public static DayInterval Create(int interval, TimeOnly[]? onTimes, Day[]? onDays)
    {
        if (interval == 0) interval = 1;

        if (onTimes == null && onDays == null)
            onTimes = [TimeOnly.Parse("00:00")];

        return new DayInterval
        {
            Interval = interval,
            OnTimes  = onTimes ?? Array.Empty<TimeOnly>(),
            OnDays   = onDays ?? Array.Empty<Day>()
        };
    }
}

public class MonthInterval
{
    //used to serialization/deserialization
    public MonthInterval() { }

    public int        Interval { get; set; }
    public int?       OnDay    { get; set; }
    public Day?       OnFirst  { get; set; }
    public TimeOnly[] OnTimes  { get; set; } = [];
    public Month[]    OnMonths { get; set; } = [];

    public static MonthInterval Create(int interval, int? onDay, Day? onfirst, TimeOnly[]? onTimes, Month[]? onMonths)
    {
        if (interval == 0) interval = 1;

        if (onDay == null && onfirst == null)
            onDay = 1;

        onTimes ??= [TimeOnly.Parse("00:00")];

        return new MonthInterval
        {
            Interval = interval,
            OnDay    = onDay,
            OnFirst  = onfirst,
            OnTimes  = onTimes,
            OnMonths = onMonths ?? Array.Empty<Month>()
        };
    }
}

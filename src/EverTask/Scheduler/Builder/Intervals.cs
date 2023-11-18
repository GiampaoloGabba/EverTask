namespace EverTask.Scheduler.Builder;

public class MinuteInterval
{
    private MinuteInterval() { }
    public int Interval { get; private set; }
    public int OnSecond { get; private set; }

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
    private DayInterval() { }

    public int        Interval { get; private set; } = 1;
    public TimeOnly[] OnTimes  { get; private set; } = [];
    public Day[]      OnDays   { get; private set; } = Array.Empty<Day>();

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
    private MonthInterval() { }

    public int        Interval { get; private set; }
    public int?       OnDay    { get; private set; }
    public Day?       OnFirst  { get; private set; }
    public TimeOnly[] OnTimes  { get; private set; } = [];
    public Month[]    OnMonths { get; private set; } = [];

    public static MonthInterval Create(int interval, int? onDay, Day? onfirst, TimeOnly[]? onTimes, Month[]? onMonths)
    {
        if (interval == 0) interval = 1;

        if (onDay == null && onfirst == null)
            onDay = 1;

        onTimes ??= [TimeOnly.Parse("00:00")];

        return new MonthInterval
        {
            Interval = interval,
            OnDay = onDay,
            OnFirst = onfirst,
            OnTimes = onTimes,
            OnMonths = onMonths ?? Array.Empty<Month>()
        };
    }
}

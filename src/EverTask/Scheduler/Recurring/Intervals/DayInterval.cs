namespace EverTask.Scheduler.Recurring.Intervals;

public class DayInterval : IInterval
{
    //used for serialization/deserialization
    [JsonConstructor]
    public DayInterval() { }

    public DayInterval(int interval)
    {
        Interval = interval;
    }

    public DayInterval(int interval, DayOfWeek[] onDays)
    {
        Interval = interval;
        OnDays   = onDays.Distinct().ToArray();
    }

    private TimeOnly[] _onTimes = [TimeOnly.Parse("00:00")];

    public int         Interval { get; init; } = 1;
    public TimeOnly[]  OnTimes
    {
        get => _onTimes;
        set => _onTimes = value.OrderBy(t => t).ToArray(); // Always keep sorted
    }
    public DayOfWeek[] OnDays   { get; internal set; } = Array.Empty<DayOfWeek>();

    public void Validate()
    {
        if (Interval == 0 && !OnDays.Any())
            throw new ArgumentException("Invalid Day Interval, you must specify at least one day.",
                nameof(DayInterval));
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        Validate();

        // OnDays (top-level OnDays(...) builds DayInterval(0, days)): fire on EVERY listed day of week,
        // every week (CU7) — not once on the first matching day.
        if (OnDays.Any())
            return current.NextDayOfWeekSlot(OnDays, OnTimes, weekStride: 1);

        // No specific days: every Interval days, at the configured time(s).
        var nextDay = current.AddDays(Interval);
        return nextDay.GetNextRequestedTime(current, OnTimes);
    }
}

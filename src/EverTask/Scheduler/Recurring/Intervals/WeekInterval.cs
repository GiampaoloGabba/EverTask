namespace EverTask.Scheduler.Recurring.Intervals;

public class WeekInterval : IInterval
{
    //used for serialization/deserialization
    [JsonConstructor]
    public WeekInterval() { }

    public WeekInterval(int interval)
    {
        Interval = interval;
    }

    public WeekInterval(int interval, DayOfWeek[] onDays)
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
    public DayOfWeek[] OnDays   { get; internal set; } = [];

    public void Validate()
    {
        if (Interval == 0 && !OnDays.Any())
            throw new ArgumentException("Invalid Week Interval, you must specify at least one day.",
                nameof(WeekInterval));
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        Validate();

        // OnDays: fire on EVERY listed day of the week, advancing by Interval weeks only when the
        // current week's slots are exhausted (CU7) — not once per week on the first matching day.
        if (OnDays.Any())
            return current.NextDayOfWeekSlot(OnDays, OnTimes, weekStride: Interval);

        // No specific days: once every Interval weeks, on the same day-of-week, at the configured time.
        var nextWeek = current.AddDays(7 * Interval);
        return nextWeek.GetNextRequestedTime(current, OnTimes, false);
    }
}

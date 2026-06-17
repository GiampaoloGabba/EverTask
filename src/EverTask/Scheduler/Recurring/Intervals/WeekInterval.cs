namespace EverTask.Scheduler.Recurring.Intervals;

public class WeekInterval : IInterval
{
    //used for serialization/deserialization
    [System.Text.Json.Serialization.JsonConstructor]
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
        // Null-tolerant (P1-1): a persisted "OnTimes":null must not crash the deserialize — default to
        // midnight, like the field initializer. An EMPTY array stays empty (an already-safe "no time-of-day
        // constraint" state), so it must NOT be rewritten. Otherwise keep sorted.
        set => _onTimes = value is null ? [new TimeOnly(0, 0)] : value.OrderBy(t => t).ToArray();
    }
    // public set (coherent with MonthInterval): an internal setter is silently dropped by STJ on read,
    // losing the OnDays schedule constraint on recovery (F1/B2).
    public DayOfWeek[] OnDays   { get; set; } = [];

    public void Validate()
    {
        if (Interval < 0)
            throw new ArgumentException("Invalid Week Interval, the interval cannot be negative.",
                nameof(WeekInterval));

        if (Interval == 0 && !OnDays.Any())
            throw new ArgumentException("Invalid Week Interval, you must specify at least one day.",
                nameof(WeekInterval));

        // OnDays must contain only defined DayOfWeek values (0-6) — an out-of-range value deserializes but
        // throws downstream at NextDayOfWeekSlot (P1-2).
        foreach (var day in OnDays)
            if (!Enum.IsDefined(typeof(DayOfWeek), day))
                throw new ArgumentException($"Invalid Week Interval, '{(int)day}' is not a valid day of week (0-6).",
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

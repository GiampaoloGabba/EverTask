namespace EverTask.Scheduler.Recurring.Intervals;

public class DayInterval : IInterval
{
    //used for serialization/deserialization
    [System.Text.Json.Serialization.JsonConstructor]
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
        // Null-tolerant (P1-1): a persisted "OnTimes":null must not crash the deserialize (the setter used to
        // dereference value) — default to midnight, like the field initializer. An EMPTY array stays empty: it
        // is an already-safe "no time-of-day constraint" state (GetNextRequestedTime keeps the time;
        // NextDayOfWeekSlot defaults to midnight), so it must NOT be rewritten. Otherwise keep sorted.
        set => _onTimes = value is null ? [new TimeOnly(0, 0)] : value.OrderBy(t => t).ToArray();
    }
    // public set (coherent with MonthInterval.OnDays/OnMonths): an internal setter is silently dropped by
    // STJ on read, losing the OnDays schedule constraint on recovery (F1/B2).
    public DayOfWeek[] OnDays   { get; set; } = Array.Empty<DayOfWeek>();

    public void Validate()
    {
        if (Interval < 0)
            throw new ArgumentException("Invalid Day Interval, the interval cannot be negative.",
                nameof(DayInterval));

        if (Interval == 0 && !OnDays.Any())
            throw new ArgumentException("Invalid Day Interval, you must specify at least one day.",
                nameof(DayInterval));

        // OnDays must contain only defined DayOfWeek values (0-6): a persisted out-of-range value (e.g. 99)
        // deserializes but throws downstream at NextDayOfWeekSlot — validate it here so recovery poisons the
        // row cleanly instead (P1-2).
        foreach (var day in OnDays)
            if (!Enum.IsDefined(typeof(DayOfWeek), day))
                throw new ArgumentException($"Invalid Day Interval, '{(int)day}' is not a valid day of week (0-6).",
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

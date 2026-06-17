namespace EverTask.Scheduler.Recurring.Intervals;

public class MonthInterval : IInterval
{
    //used for serialization/deserialization
    [System.Text.Json.Serialization.JsonConstructor]
    public MonthInterval() { }

    public MonthInterval(int interval)
    {
        Interval = interval;
    }

    public MonthInterval(int interval, int[] onMonths)
    {
        Interval = interval;
        OnMonths = onMonths.Distinct().ToArray();
    }

    private TimeOnly[] _onTimes = [TimeOnly.Parse("00:00")];

    public int        Interval { get; init; }
    public int?       OnDay    { get; set; }
    public int[]      OnDays   { get; set; } = [];
    public DayOfWeek? OnFirst  { get; set; }
    public TimeOnly[] OnTimes
    {
        get => _onTimes;
        // Null-tolerant (P1-1): a persisted "OnTimes":null must not crash the deserialize — default to
        // midnight, like the field initializer. An EMPTY array stays empty (an already-safe "no time-of-day
        // constraint" state), so it must NOT be rewritten. Otherwise keep sorted.
        set => _onTimes = value is null ? [new TimeOnly(0, 0)] : value.OrderBy(t => t).ToArray();
    }
    // Settable (like OnDays) so System.Text.Json repopulates it on deserialization — a get-only property is
    // silently dropped on round-trip, losing the month constraint (F11).
    public int[]      OnMonths { get; set; } = [];

    public void Validate()
    {
        if (Interval < 0)
            throw new ArgumentException("Invalid Month Interval, the interval cannot be negative.",
                nameof(MonthInterval));

        if (Interval == 0 && !OnMonths.Any())
            throw new ArgumentException("Invalid Month Interval, you must specify at least one month.",
                nameof(MonthInterval));

        // Out-of-range month/day selectors deserialize but throw downstream at NextValidMonth/NextValidDay —
        // validate them here so recovery poisons the row cleanly (B2/gap #2).
        foreach (var month in OnMonths)
            if (month < 1 || month > 12)
                throw new ArgumentException($"Invalid Month Interval, '{month}' is not a valid month (1-12).",
                    nameof(MonthInterval));

        foreach (var day in OnDays)
            if (day < 1 || day > 31)
                throw new ArgumentException($"Invalid Month Interval, '{day}' is not a valid day of month (1-31).",
                    nameof(MonthInterval));

        if (OnDay is < 1 or > 31)
            throw new ArgumentException($"Invalid Month Interval, OnDay '{OnDay}' is not a valid day of month (1-31).",
                nameof(MonthInterval));
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        Validate();

        var nextMonth = current.AddMonths(Interval);

        //The checks for a valid day can change the current month if the specified days are no longer in this month,
        //so first we check the requested day, then we check if the final month is inthe valid months specificed.
        //If not, we find the next valid month maintaining the day.
        if (OnFirst != null)
        {
            nextMonth = nextMonth.FindFirstOccurrenceOfDayOfWeekInMonth(OnFirst.Value);
        }
        else if (OnDays.Any())
        {
            nextMonth = nextMonth.NextValidDay(OnDays);
        }
        else if (OnDay.HasValue)
        {
            // Adjust day directly - we've already added Interval months, so don't add more
            var daysInMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
            var validDay = Math.Min(OnDay.Value, daysInMonth);
            nextMonth = nextMonth.Adjust(day: validDay);
        }
        // If no day specification (OnFirst, OnDays, OnDay), keep the day from AddMonths()

        if (OnMonths.Any())
            nextMonth = nextMonth.NextValidMonth(OnMonths);

        return nextMonth.GetNextRequestedTime(current, OnTimes, false);
    }
}

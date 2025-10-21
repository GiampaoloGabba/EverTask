using Newtonsoft.Json;

namespace EverTask.Scheduler.Recurring.Intervals;

public class MonthInterval : IInterval
{
    //used for serialization/deserialization
    [JsonConstructor]
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
        set => _onTimes = value.OrderBy(t => t).ToArray(); // Always keep sorted
    }
    public int[]      OnMonths { get; } = [];

    public void Validate()
    {
        if (Interval == 0 && !OnMonths.Any())
            throw new ArgumentException("Invalid Month Interval, you must specify at least one month.",
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

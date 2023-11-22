namespace EverTask.Scheduler.Recurring;

public static class DateTimeOffsetExtensions
{
    public static DateTimeOffset AdjustDayToValidMonthDay(this DateTimeOffset nextMonth, int day)
    {
        // Check if the day is valid for the given month
        var daysInMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
        if (day > daysInMonth)
        {
            // If the day is not valid, set it to the last day of the month
            day = daysInMonth;
        }

        var newMonth = nextMonth.Adjust(day: day);

        if (newMonth < nextMonth)
            newMonth = nextMonth.AddMonths(1);

        return newMonth;
    }

    public static DateTimeOffset GetNextRequestedTime(this DateTimeOffset nextDay, DateTimeOffset current, TimeOnly[] onTimes, bool addDays = true)
    {
        if (!onTimes.Any()) return nextDay;

        var orderedOnTimes  = onTimes.OrderBy(t => t).ToArray();
        var currentTimeOnly = TimeOnly.FromDateTime(current.DateTime);

        // The default for TimeOnly is midnight, so we need to check the array index to know if there is a date specified by a user
        var nextTimeIndex = Array.FindIndex(orderedOnTimes, t => t > currentTimeOnly);

        if (nextTimeIndex == -1)
        {
            if (addDays)
                nextDay       = nextDay.AddDays(1);
            nextTimeIndex = 0;
        }

        var nextTime = orderedOnTimes[nextTimeIndex];
        return nextDay.Adjust(hour: nextTime.Hour, minute: nextTime.Minute, second: nextTime.Second);

    }

    public static DateTimeOffset NextValidDayOfWeek(this DateTimeOffset dateTime, DayOfWeek[] validDays)
    {
        while (!validDays.Contains(dateTime.DayOfWeek))
        {
            dateTime = dateTime.AddDays(1);
        }
        return dateTime;
    }

    public static DateTimeOffset NextValidDay(this DateTimeOffset dateTime, int[] validDays)
    {
        while (!validDays.Contains(dateTime.Day))
        {
            dateTime = dateTime.AddDays(1);
        }
        return dateTime;
    }

    public static DateTimeOffset NextValidHour(this DateTimeOffset dateTime, int[] validHour)
    {
        while (!validHour.Contains(dateTime.Hour))
        {
            dateTime = dateTime.AddHours(1);
        }
        return dateTime;
    }

    public static DateTimeOffset NextValidMonth(this DateTimeOffset dateTime, int[] validMonths)
    {
        while (!validMonths.Contains(dateTime.Month))
        {
            dateTime = dateTime.AddMonths(1);
        }
        return dateTime;
    }

    public static DateTimeOffset FindFirstOccurrenceOfDayOfWeekInMonth(this DateTimeOffset dateTime, DayOfWeek dayOfWeek)
    {
        while (dateTime.DayOfWeek != dayOfWeek)
        {
            dateTime = dateTime.AddDays(1);

            // If you moved into the next month, reset to the first day of that month
            if (dateTime.Day == DateTime.DaysInMonth(dateTime.Year, dateTime.Month))
            {
                dateTime = dateTime.Adjust(day: 1);
            }
        }
        return dateTime;
    }

    public static TimeOnly ToUniversalTime(this TimeOnly time)
    {
        var datetime    = DateTimeOffset.Now.Adjust(hour: time.Hour, minute: time.Minute, second: time.Second);
        var utcDateTime = datetime.ToUniversalTime().DateTime;
        return TimeOnly.FromDateTime(utcDateTime);
    }

    public static DateTimeOffset Adjust(this DateTimeOffset dateTime, int? day = null, int? hour = null, int? minute = null, int? second = null)
    {
        return new DateTimeOffset(
            dateTime.Year,
            dateTime.Month,
            day ?? dateTime.Day,
            hour ?? dateTime.Hour,
            minute ?? dateTime.Minute,
            second ?? dateTime.Second,
            dateTime.Offset);
    }

}

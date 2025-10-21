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

        // onTimes is guaranteed to be sorted by the OnTimes property setter in DayInterval/MonthInterval
        // This eliminates repeated sorting on every call
        var currentTimeOnly = TimeOnly.FromDateTime(current.DateTime);

        // If nextDay is on a different day than current, we can use >= comparison
        // Otherwise, use > to ensure we get a time after the current time
        bool isDifferentDay = nextDay.Date != current.Date;

        // The default for TimeOnly is midnight, so we need to check the array index to know if there is a date specified by a user
        var nextTimeIndex = Array.FindIndex(onTimes, t => isDifferentDay ? t >= currentTimeOnly : t > currentTimeOnly);

        if (nextTimeIndex == -1)
        {
            if (addDays)
                nextDay       = nextDay.AddDays(1);
            nextTimeIndex = 0;
        }

        var nextTime = onTimes[nextTimeIndex];
        return nextDay.Adjust(hour: nextTime.Hour, minute: nextTime.Minute, second: nextTime.Second);

    }

    public static DateTimeOffset NextValidDayOfWeek(this DateTimeOffset dateTime, DayOfWeek[] validDays)
    {
        if (validDays.Length == 0)
            throw new ArgumentException("validDays cannot be empty", nameof(validDays));

        const int maxIterations = 7; // Only 7 days in a week
        for (int i = 0; i < maxIterations; i++)
        {
            if (validDays.Contains(dateTime.DayOfWeek))
                return dateTime;
            dateTime = dateTime.AddDays(1);
        }

        throw new InvalidOperationException($"Could not find valid day of week in {maxIterations} iterations");
    }

    public static DateTimeOffset NextValidDay(this DateTimeOffset dateTime, int[] validDays)
    {
        if (validDays.Length == 0)
            throw new ArgumentException("validDays cannot be empty", nameof(validDays));

        // Validate all days are 1-31
        if (validDays.Any(d => d < 1 || d > 31))
            throw new ArgumentException("validDays must contain values between 1 and 31", nameof(validDays));

        var startMonth = dateTime.Month;
        var startYear = dateTime.Year;
        var daysInMonth = DateTime.DaysInMonth(startYear, startMonth);

        // Try to find a valid day in the current month
        for (int i = 0; i < daysInMonth; i++)
        {
            if (validDays.Contains(dateTime.Day))
                return dateTime;

            dateTime = dateTime.AddDays(1);

            // If we've moved to next month, break and handle below
            if (dateTime.Month != startMonth)
                break;
        }

        // If no valid day found in current month, move to first day of next month and recurse
        dateTime = new DateTimeOffset(dateTime.Year, dateTime.Month, 1,
            dateTime.Hour, dateTime.Minute, dateTime.Second, dateTime.Offset);
        return NextValidDay(dateTime, validDays);
    }

    public static DateTimeOffset NextValidHour(this DateTimeOffset dateTime, int[] validHour)
    {
        if (validHour.Length == 0)
            throw new ArgumentException("validHour cannot be empty", nameof(validHour));

        if (validHour.Any(h => h < 0 || h > 23))
            throw new ArgumentException("validHour must contain values between 0 and 23", nameof(validHour));

        const int maxIterations = 24;
        for (int i = 0; i < maxIterations; i++)
        {
            if (validHour.Contains(dateTime.Hour))
                return dateTime;
            dateTime = dateTime.AddHours(1);
        }

        throw new InvalidOperationException($"Could not find valid hour in {maxIterations} iterations");
    }

    public static DateTimeOffset NextValidMonth(this DateTimeOffset dateTime, int[] validMonths)
    {
        if (validMonths.Length == 0)
            throw new ArgumentException("validMonths cannot be empty", nameof(validMonths));

        if (validMonths.Any(m => m < 1 || m > 12))
            throw new ArgumentException("validMonths must contain values between 1 and 12", nameof(validMonths));

        const int maxIterations = 12;
        for (int i = 0; i < maxIterations; i++)
        {
            if (validMonths.Contains(dateTime.Month))
                return dateTime;
            dateTime = dateTime.AddMonths(1);
        }

        throw new InvalidOperationException($"Could not find valid month in {maxIterations} iterations");
    }

    public static DateTimeOffset FindFirstOccurrenceOfDayOfWeekInMonth(this DateTimeOffset dateTime, DayOfWeek dayOfWeek)
    {
        // Start from first day of current month
        var firstOfMonth = dateTime.Adjust(day: 1);

        const int maxIterations = 7; // First occurrence must be within first 7 days
        for (int i = 0; i < maxIterations; i++)
        {
            if (firstOfMonth.DayOfWeek == dayOfWeek)
                return firstOfMonth;
            firstOfMonth = firstOfMonth.AddDays(1);
        }

        throw new InvalidOperationException($"Could not find {dayOfWeek} in first week of month");
    }

    public static TimeOnly ToUniversalTime(this TimeOnly time)
    {
        // Since EverTask works internally in UTC, we interpret TimeOnly as UTC time.
        // This makes the API consistent and timezone-independent.
        // If users want to specify local time, they should convert it themselves before passing it.
        var datetime    = DateTimeOffset.UtcNow.Adjust(hour: time.Hour, minute: time.Minute, second: time.Second);
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

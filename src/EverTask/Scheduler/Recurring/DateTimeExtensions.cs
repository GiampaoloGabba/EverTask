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
        if (onTimes.Length == 0) return nextDay;

        // onTimes is guaranteed to be sorted by the OnTimes property setter in DayInterval/MonthInterval
        // This eliminates repeated sorting on every call
        var currentTimeOnly = TimeOnly.FromDateTime(current.DateTime);

        // If nextDay is on a different day than current, we can use >= comparison
        // Otherwise, use > to ensure we get a time after the current time
        var isDifferentDay = nextDay.Date != current.Date;

        // The default for TimeOnly is midnight, so we need to check the array index to know if there is a date specified by a user
        var nextTimeIndex = Array.FindIndex(onTimes, t => isDifferentDay ? t >= currentTimeOnly : t > currentTimeOnly);

        if (nextTimeIndex == -1)
        {
            // No matching onTime on the target day. Advance another day ONLY when we are still on the
            // ORIGINAL day (same-day, no later slot). When nextDay is already a strictly-later day every
            // onTime on it is valid, so use the earliest — bumping again would drop a whole day, which is
            // what made a DayInterval whose onTimes were all before the reference time return day+2 (L26).
            if (addDays && !isDifferentDay)
                nextDay = nextDay.AddDays(1);
            nextTimeIndex = 0;
        }

        var nextTime = onTimes[nextTimeIndex];
        return nextDay.WithTimeOfDay(nextTime);

    }

    /// <summary>
    /// Next calendar slot strictly after <paramref name="current"/> that falls on a day in
    /// <paramref name="onDays"/> at a time in <paramref name="onTimes"/> (sorted ascending). Fires on
    /// EVERY listed day of the current week, advancing by <paramref name="weekStride"/> weeks only once
    /// the current week's remaining slots are exhausted — so e.g. OnDays(Mon, Wed, Fri) fires three
    /// times a week, not once (CU7).
    /// </summary>
    public static DateTimeOffset NextDayOfWeekSlot(this DateTimeOffset current, DayOfWeek[] onDays,
                                                   TimeOnly[] onTimes, int weekStride)
    {
        if (onDays.Length == 0)
            throw new ArgumentException("onDays cannot be empty", nameof(onDays));

        var times = onTimes.Length > 0 ? onTimes : [new TimeOnly(0, 0)];
        if (weekStride < 1)
            weekStride = 1;

        // 1. Remaining slots in the CURRENT week (from current's day through Saturday): the first slot
        //    strictly after `current`.
        var daysToWeekEnd = DayOfWeek.Saturday - current.DayOfWeek; // Sunday=0 .. Saturday=6
        for (var i = 0; i <= daysToWeekEnd; i++)
        {
            var slot = FirstSlotOnDay(current.AddDays(i), onDays, times, after: current);
            if (slot.HasValue)
                return slot.Value;
        }

        // 2. Current week exhausted → jump `weekStride` weeks and take the first slot of that week.
        var nextWeekStart = current.AddDays(-(int)current.DayOfWeek + 7 * weekStride); // Sunday of target week
        for (var i = 0; i < 7; i++)
        {
            var slot = FirstSlotOnDay(nextWeekStart.AddDays(i), onDays, times, after: null);
            if (slot.HasValue)
                return slot.Value;
        }

        // onDays is non-empty, so a slot is always found above; this only satisfies the compiler.
        throw new InvalidOperationException("Could not compute the next day-of-week slot");
    }

    private static DateTimeOffset? FirstSlotOnDay(DateTimeOffset day, DayOfWeek[] onDays, TimeOnly[] onTimes,
                                                  DateTimeOffset? after)
    {
        if (!onDays.Contains(day.DayOfWeek))
            return null;

        foreach (var time in onTimes) // sorted ascending
        {
            var slot = day.WithTimeOfDay(time);
            if (after == null || slot > after.Value)
                return slot;
        }

        return null;
    }

    public static DateTimeOffset NextValidDayOfWeek(this DateTimeOffset dateTime, DayOfWeek[] validDays)
    {
        if (validDays.Length == 0)
            throw new ArgumentException("validDays cannot be empty", nameof(validDays));

        const int maxIterations = 7; // Only 7 days in a week
        for (var i = 0; i < maxIterations; i++)
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
        for (var i = 0; i < daysInMonth; i++)
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
        for (var i = 0; i < maxIterations; i++)
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
        for (var i = 0; i < maxIterations; i++)
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
        for (var i = 0; i < maxIterations; i++)
        {
            if (firstOfMonth.DayOfWeek == dayOfWeek)
                return firstOfMonth;
            firstOfMonth = firstOfMonth.AddDays(1);
        }

        throw new InvalidOperationException($"Could not find {dayOfWeek} in first week of month");
    }

    /// <summary>
    /// <paramref name="day"/>'s date carrying <paramref name="time"/> as its time of day, with the offset
    /// <paramref name="day"/> already had.
    /// </summary>
    /// <remarks>
    /// The whole <see cref="TimeOnly"/>, sub-second included. <c>AtTime</c>/<c>AtTimes</c> store what the
    /// caller passed verbatim (T12), so the grid has to be able to land on it; the
    /// <see cref="Adjust(DateTimeOffset,int?,int?,int?,int?)"/> call this replaced silently rounded every slot
    /// down to the second. Nothing the builder could express before carried a sub-second component, so for
    /// every schedule written until now the two are the same instant.
    /// </remarks>
    internal static DateTimeOffset WithTimeOfDay(this DateTimeOffset day, TimeOnly time) =>
        new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, day.Offset).Add(time.ToTimeSpan());

    /// <summary>
    /// Returns <paramref name="time"/> unchanged, less any sub-second component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deprecated (T12): do not call it, and do not use it on a time you are about to schedule.</b> It
    /// dates from when EverTask read every schedule on the UTC clock and a "time of day" had to be declared
    /// as one. It never converted anything — it rebuilt the value from today's UTC date, whose offset is
    /// zero — so the only thing it has ever done is drop the milliseconds. It is kept, unmarked, because
    /// <c>[Obsolete]</c> would fail the build of every consumer compiling warnings-as-errors (R13); it will
    /// be removed in a future major.
    /// </para>
    /// <para>
    /// A time of day is now read on the schedule's own zone: pass the local time you mean to
    /// <c>AtTime</c>/<c>AtTimes</c> and name the zone with <c>InTimeZone</c>. Converting it yourself freezes
    /// one offset into the schedule and is wrong for half the year — see
    /// <c>docs/recurring-tasks/time-zones.md</c>.
    /// </para>
    /// </remarks>
    public static TimeOnly ToUniversalTime(this TimeOnly time) =>
        new(time.Hour, time.Minute, time.Second);

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

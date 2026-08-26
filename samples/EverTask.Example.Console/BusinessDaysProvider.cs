using EverTask.Abstractions;

namespace EverTask.Example.Console;

/// <summary>
/// An occurrence provider: the grid of a schedule that no interval and no cron expression can express.
/// This one fires at 09:00 on working days and skips a (hard-coded) holiday list — in a real application the
/// holidays would come from the database this class would take a repository for.
/// </summary>
/// <remarks>
/// Registered with <c>AddOccurrenceProvider&lt;BusinessDaysProvider&gt;("business-days")</c> and selected with
/// <c>Schedule().UseOccurrenceProvider("business-days")</c>. Only the KEY is persisted on the schedule row,
/// never this type's name, so renaming or moving the class does not orphan the schedules that use it.
/// </remarks>
public sealed class BusinessDaysProvider : INextOccurrenceProvider
{
    private static readonly TimeSpan RunAt = TimeSpan.FromHours(9);

    /// <summary>
    /// The fixed-date holidays, as month and day. NOT as whole dates: a set built from
    /// <c>DateTime.UtcNow.Year</c> is pinned to the year the process STARTED in, so its 1 January is always
    /// in the past — the one that matters, next year's, is not in the set at all — and a host still running
    /// on 2 January would happily call 25 December of the following year a working day. A real provider reads
    /// its calendar from a repository, and the movable feasts with it.
    /// </summary>
    private static readonly HashSet<(int Month, int Day)> Holidays = [(12, 25), (12, 26), (1, 1)];

    /// <summary>
    /// The same instant always gets the same answer, which is what a catch-up with
    /// <see cref="CatchUpOverflowPolicy.SkipOldest"/> needs in order to probe the grid.
    /// </summary>
    public bool IsDeterministic => true;

    public ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                             CancellationToken cancellationToken = default)
    {
        var zone  = ResolveZone(request.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(request.AfterUtc, zone);

        // Today's slot if it is still ahead, otherwise start looking tomorrow. The answer has to be STRICTLY
        // after the instant asked about, and the request may name any instant — not only one this provider
        // returned before.
        var day = DateOnly.FromDateTime(local.DateTime);

        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                Holidays.Contains((day.Month, day.Day)))
            {
                continue;
            }

            var wall   = day.ToDateTime(TimeOnly.MinValue).Add(RunAt);
            var offset = zone.GetUtcOffset(wall);
            var slot   = new DateTimeOffset(wall, offset).ToUniversalTime();

            if (slot > request.AfterUtc)
                return new ValueTask<DateTimeOffset?>(slot);
        }

        // No further occurrence: the series ends here.
        return new ValueTask<DateTimeOffset?>((DateTimeOffset?)null);
    }

    private static TimeZoneInfo ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrEmpty(timeZoneId))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

using System.Globalization;

namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Maps a running schedule's cursor onto a new definition
/// (<see cref="Abstractions.RescheduleMode.RebaseFromCursor"/>).
/// </summary>
/// <remarks>
/// The cursor is an INSTANT of the old grid, so it is never reused verbatim: what carries over is the nominal
/// period it fell in — read on the old definition's clock — plus its POSITION inside that period, and the new
/// cursor is the new definition's occurrence at that same position on the new clock. The two definitions must
/// have the same shape, and a period the new definition has no slot in is refused rather than answered from
/// the next one, which would skip a whole period of work or replay one.
/// </remarks>
internal static class ScheduleRebase
{
    /// <summary>
    /// The cursor <paramref name="replacement"/> should start from, given that <paramref name="current"/> was
    /// standing at <paramref name="cursorUtc"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The two definitions cannot be rebased onto each other, or the period holds no slot of the new one at
    /// the position the cursor had reached.
    /// </exception>
    internal static DateTimeOffset Rebase(RecurringTask current, RecurringTask replacement,
                                          DateTimeOffset cursorUtc)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);

        RequireSameShape(current, replacement);

        var kind = current.PeriodKind;

        // A plain cadence produces the same instants in every zone and its shape is pinned above, so the
        // period is the cursor and there is nothing to move.
        if (kind == SchedulePeriodKind.Instant)
            return WithinBound(replacement, cursorUtc);

        // A week or month cadence that names no day inside its period carries that day on the CURSOR, not in
        // the definition, so the period that has to survive is the cursor's DAY (see CursorCarriesTheDay).
        var cursorCarriesTheDay = CursorCarriesTheDay(current);

        if (cursorCarriesTheDay)
            kind = SchedulePeriodKind.Day;

        var wall                     = WallOf(current, cursorUtc);
        var (periodStart, periodEnd) = NominalPeriod(kind, wall);

        // The same nominal period, read on the NEW definition's clock: a schedule that changes zone keeps the
        // day it was on, instead of being carried to whatever instant its old offset made.
        var replacementZone = replacement.GoverningZone ?? TimeZoneInfo.Utc;
        var startUtc        = ToInstant(periodStart, replacementZone);
        var endUtc          = ToInstant(periodEnd, replacementZone);

        // WHERE INSIDE the period the cursor stood, counted in slots of the old definition. Taking the first
        // slot instead would rebase BACKWARD onto an occurrence that has already run whenever a period holds
        // more than one, replaying it and spending one more of MaxRuns.
        var index = cursorCarriesTheDay
                        ? PositionAmong(OnTimesOf(current), TimeOnly.FromDateTime(wall))
                        : PositionInPeriod(current, ToInstant(periodStart, current.GoverningZone ?? TimeZoneInfo.Utc),
                            ToInstant(periodEnd, current.GoverningZone ?? TimeZoneInfo.Utc), cursorUtc);

        // Inclusive of the period's own start: the cursor is a slot that has not fired yet, so the slot at its
        // own position is a legitimate answer. Every other question the grid answers is "strictly after".
        var slot = cursorCarriesTheDay
                       ? PlaceInDay(replacement, periodStart, replacementZone, wall, index)
                       : NthSlotFrom(replacement, startUtc, index);

        if (slot is not { } value || value < startUtc || value >= endUtc)
        {
            var missing = index == 0
                              ? "has no occurrence inside"
                              : string.Create(CultureInfo.InvariantCulture,
                                  $"holds fewer than {index + 1} occurrence(s) inside");

            throw new InvalidOperationException(
                $"The new schedule {missing} the {Describe(kind)} the cursor {cursorUtc:O} belongs to, and a " +
                "rebase never crosses into the next one — that would skip a whole period of work or replay " +
                "one. Reschedule with RescheduleMode.RecalculateFromNow instead.");
        }

        return WithinBound(replacement, value);
    }

    /// <summary>
    /// The slot <paramref name="index"/> positions into the period <paramref name="startUtc"/> opens.
    /// </summary>
    /// <remarks>
    /// The successors are asked with the BOUNDS IGNORED, because <see cref="WithinBound"/> is what applies
    /// <c>RunUntil</c> and it has to see the slot the cursor's position names rather than the last one before
    /// the bound.
    /// </remarks>
    private static DateTimeOffset? NthSlotFrom(RecurringTask replacement, DateTimeOffset startUtc, int index)
    {
        var slot = replacement.FirstOccurrenceOnOrAfter(startUtc);

        for (var i = 0; i < index && slot is { } value; i++)
            slot = replacement.NextGridOccurrenceAfter(value);

        return slot;
    }

    /// <summary>
    /// Where <paramref name="cursorUtc"/> stands among the slots its own period holds, counted on
    /// <paramref name="definition"/>'s grid with the termination bounds ignored.
    /// </summary>
    /// <remarks>
    /// A cursor PAST every slot of its period was not produced by the grid — a row written by hand, a seeded
    /// backlog — and clamps to the last position rather than to one that does not exist.
    /// </remarks>
    private static int PositionInPeriod(RecurringTask definition, DateTimeOffset periodStartUtc,
                                        DateTimeOffset periodEndUtc, DateTimeOffset cursorUtc)
    {
        var slot  = definition.FirstGridOccurrenceOnOrAfter(periodStartUtc);
        var index = 0;
        var held  = 0;

        while (slot is { } value && value < periodEndUtc)
        {
            held++;

            if (value < cursorUtc)
                index++;

            slot = definition.NextGridOccurrenceAfter(value);
        }

        return held == 0 ? 0 : Math.Min(index, held - 1);
    }

    /// <summary>
    /// The slot for a cadence whose day rides on the cursor: that same day, at the time of day
    /// <paramref name="index"/> positions into the replacement's own times.
    /// </summary>
    /// <remarks>
    /// An EMPTY <c>OnTimes</c> constrains no time at all — the interval hands the probe's own time of day
    /// straight back — so there the cursor carries the time too and it is kept verbatim.
    /// </remarks>
    private static DateTimeOffset? PlaceInDay(RecurringTask replacement, DateTime periodStart, TimeZoneInfo zone,
                                              DateTime cursorWall, int index)
    {
        if (OnTimesOf(replacement) is not { Length: > 0 } times)
            return ToInstant(periodStart.Add(TimeOnly.FromDateTime(cursorWall).ToTimeSpan()), zone);

        return index < times.Length
                   ? ToInstant(periodStart.Add(times[index].ToTimeSpan()), zone)
                   : null;
    }

    /// <summary>Where <paramref name="time"/> stands among <paramref name="times"/>.</summary>
    /// <remarks>
    /// <c>OnTimes</c> is kept sorted by its own setter, so counting the ones before it gives the slot's ordinal
    /// in the day. Clamped like <see cref="PositionInPeriod"/>: a cursor past every time stands at the last one.
    /// </remarks>
    private static int PositionAmong(TimeOnly[] times, TimeOnly time)
    {
        var index = 0;

        while (index < times.Length && times[index] < time)
            index++;

        return times.Length == 0 ? 0 : Math.Min(index, times.Length - 1);
    }

    /// <summary>The times of day of the interval that owns the period, for the day-carrying cadences.</summary>
    private static TimeOnly[] OnTimesOf(RecurringTask definition) =>
        definition.PeriodKind == SchedulePeriodKind.Month
            ? definition.MonthInterval!.OnTimes
            : definition.WeekInterval!.OnTimes;

    /// <summary>
    /// The rebased cursor, or a refusal when the replacement definition has already ended on it.
    /// </summary>
    /// <remarks>
    /// Two of the three branches above place the slot BY HAND and never pass through
    /// <see cref="RecurringTask.FirstOccurrenceOnOrAfter"/>, which is the only thing that applies the
    /// (exclusive) <c>RunUntil</c>; without this a schedule wound down with <c>RunUntil</c> would be parked at
    /// a cursor past its own end and run one more time.
    /// </remarks>
    private static DateTimeOffset WithinBound(RecurringTask replacement, DateTimeOffset slot)
    {
        if (replacement.RunUntil is not { } bound || slot < bound)
            return slot;

        throw new InvalidOperationException(
            $"The new schedule ends at {bound:O} and the cursor rebases onto {slot:O}, which is not an " +
            "occurrence it has left to run — RunUntil is exclusive. Use CancelSchedule to end a series on " +
            "purpose.");
    }

    /// <summary>
    /// True when the day an occurrence falls on comes from the CURSOR rather than from the definition, which
    /// makes that day — and not the week or the month around it — the period a rebase has to preserve.
    /// </summary>
    /// <remarks>
    /// A week cadence with no <c>OnDays</c> and every month cadence step their period first and only then
    /// apply a day selector that walks FORWARD from the day handed in, so the grid's phase lives on the
    /// cursor. Naming the whole week or month instead answers from the phase the backward probe happened to
    /// land on: a Wednesday series comes back on a Sunday, and <c>OnDays(1, 15)</c> standing on the 15th comes
    /// back on the 1st.
    /// </remarks>
    private static bool CursorCarriesTheDay(RecurringTask definition) => definition.PeriodKind switch
    {
        // A finer interval underneath decides the day itself, so the cursor is not the only thing carrying it
        // and placing the slot by hand would be guessing. Those shapes keep the grid probe.
        SchedulePeriodKind.Week => definition.WeekInterval is { OnDays.Length: 0 } && definition.DayInterval is null,

        // A month period holds exactly ONE slot however it names its day, because the cascade advances the
        // period before it selects inside it — so the day a month grid lands on is the day the cursor already
        // stands on, whichever selector produced it.
        SchedulePeriodKind.Month => definition.MonthInterval is not null
                                    && definition.WeekInterval is null && definition.DayInterval is null,

        _ => false
    };

    /// <summary>
    /// Refuses two definitions a cursor cannot be carried between, naming which half of the shape differs.
    /// </summary>
    /// <remarks>
    /// A rebase only claims to preserve WHEN INSIDE the period an occurrence falls, so everything deciding
    /// WHICH periods have occurrences at all must be identical; the time of day, the zone, the termination
    /// bounds and the misfire caps may move.
    /// </remarks>
    private static void RequireSameShape(RecurringTask current, RecurringTask replacement)
    {
        if (current.PeriodKind == SchedulePeriodKind.None || replacement.PeriodKind == SchedulePeriodKind.None)
        {
            var shape = current.Provider != null || replacement.Provider != null
                            ? "A schedule whose occurrences come from a provider"
                            : "A cron schedule";

            throw new InvalidOperationException(
                $"{shape} exposes no nominal period, so its cursor cannot be rebased onto another " +
                "definition. Reschedule with RescheduleMode.RecalculateFromNow instead.");
        }

        if (current.PeriodKind != replacement.PeriodKind || current.Semantics != replacement.Semantics)
        {
            throw new InvalidOperationException(
                $"The two schedules are anchored differently ({current.Semantics}/{current.PeriodKind} versus " +
                $"{replacement.Semantics}/{replacement.PeriodKind}), so there is no period to carry the cursor " +
                "across. Reschedule with RescheduleMode.RecalculateFromNow instead.");
        }

        if (!SameGrid(current, replacement))
        {
            throw new InvalidOperationException(
                "A rebase preserves where an occurrence falls INSIDE its period, so the cadence and the " +
                "day/month selectors have to stay the same — only the time of day, the time zone, the " +
                "termination bounds and the misfire settings may change. Reschedule with " +
                "RescheduleMode.RecalculateFromNow instead.");
        }
    }

    /// <summary>True when the two definitions select the same periods, whatever they do inside one.</summary>
    private static bool SameGrid(RecurringTask a, RecurringTask b) =>
        a.SecondInterval?.Interval == b.SecondInterval?.Interval
        && a.MinuteInterval?.Interval == b.MinuteInterval?.Interval
        && a.MinuteInterval?.OnSecond == b.MinuteInterval?.OnSecond
        && a.HourInterval?.Interval == b.HourInterval?.Interval
        && a.HourInterval?.OnMinute == b.HourInterval?.OnMinute
        && a.HourInterval?.OnSecond == b.HourInterval?.OnSecond
        && SameSelector(a.HourInterval?.OnHours, b.HourInterval?.OnHours)
        && a.DayInterval?.Interval == b.DayInterval?.Interval
        && SameSelector(a.DayInterval?.OnDays, b.DayInterval?.OnDays)
        && a.WeekInterval?.Interval == b.WeekInterval?.Interval
        && SameSelector(a.WeekInterval?.OnDays, b.WeekInterval?.OnDays)
        && a.MonthInterval?.Interval == b.MonthInterval?.Interval
        && a.MonthInterval?.OnDay == b.MonthInterval?.OnDay
        && a.MonthInterval?.OnFirst == b.MonthInterval?.OnFirst
        && SameSelector(a.MonthInterval?.OnDays, b.MonthInterval?.OnDays)
        && SameSelector(a.MonthInterval?.OnMonths, b.MonthInterval?.OnMonths);

    /// <summary>
    /// Selector equality that treats "the interval is absent" and "it selects nothing" alike, since both leave
    /// the grid to the other fields.
    /// </summary>
    private static bool SameSelector<T>(T[]? a, T[]? b) =>
        (a ?? []).SequenceEqual(b ?? []);

    /// <summary><paramref name="instant"/> read on the clock <paramref name="definition"/>'s calendar uses.</summary>
    private static DateTime WallOf(RecurringTask definition, DateTimeOffset instant) =>
        definition.GoverningZone is { } zone
            ? WallClock.ToWall(instant, zone).DateTime
            : instant.UtcDateTime;

    /// <summary>The half-open nominal period <paramref name="wall"/> falls in.</summary>
    private static (DateTime Start, DateTime End) NominalPeriod(SchedulePeriodKind kind, DateTime wall) =>
        kind switch
        {
            SchedulePeriodKind.Day => (wall.Date, wall.Date.AddDays(1)),

            // Sunday-anchored, matching the week the day-of-week walk itself steps over
            // (DateTimeOffsetExtensions.NextDayOfWeekSlot): a rebase that used a different week boundary would
            // put the cursor in a week the grid does not agree exists.
            SchedulePeriodKind.Week => (wall.Date.AddDays(-(int)wall.DayOfWeek),
                                        wall.Date.AddDays(-(int)wall.DayOfWeek).AddDays(7)),

            SchedulePeriodKind.Month => (new DateTime(wall.Year, wall.Month, 1),
                                         new DateTime(wall.Year, wall.Month, 1).AddMonths(1)),

            _ => throw new InvalidOperationException($"No nominal period is defined for {kind}.")
        };

    /// <summary>A nominal local boundary read back as an instant in <paramref name="zone"/>.</summary>
    /// <remarks>
    /// Through the same mapping the grid itself uses, so a boundary a daylight-saving gap removed becomes the
    /// first local tick that exists, rather than throwing or silently sliding by an hour.
    /// </remarks>
    private static DateTimeOffset ToInstant(DateTime wall, TimeZoneInfo zone) =>
        WallClock.ToUtc(wall, zone, DateTimeOffset.MinValue).Utc;

    private static string Describe(SchedulePeriodKind kind) => kind switch
    {
        SchedulePeriodKind.Day   => "day",
        SchedulePeriodKind.Week  => "week",
        SchedulePeriodKind.Month => "month",
        _                        => kind.ToString()
    };
}

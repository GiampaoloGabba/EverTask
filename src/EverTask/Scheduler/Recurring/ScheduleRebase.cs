using System.Globalization;

namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Maps a running schedule's cursor onto a new definition without losing the calendar period it belonged to
/// (M18, <see cref="Abstractions.RescheduleMode.RebaseFromCursor"/>).
/// </summary>
/// <remarks>
/// The cursor is an INSTANT of the old grid and it does not belong to the new one, so it is never reused as it
/// stands: moving a daily 09:00 job to 10:00 that way would leave it at 09:00 for one more day, and moving a
/// Rome schedule to another zone would move the day itself. What carries over is the nominal PERIOD — the day,
/// the week or the month the old cursor fell in, read on the old definition's clock — together with the
/// cursor's POSITION inside it: the new cursor is the new definition's occurrence at that same position, read
/// on the new clock. Naming only the period and taking its first slot is the same thing while a period holds
/// one slot, and a rewind onto work already done as soon as it holds two.
/// <para>
/// A week cadence that names no day, and EVERY month cadence, are the shapes whose period is smaller than its
/// name suggests: they fire once per period on the day their anchor was on, so the day itself rides on the
/// cursor and it is the day — not the week or the month around it — that has to survive.
/// </para>
/// <para>
/// Deliberately narrow. The two definitions must have the same shape, because a rebase across a different
/// cadence has no meaning that could be defended, and the period is never crossed: a period the new definition
/// has no slot in is refused rather than answered from the next one, which would silently skip a whole period
/// of work or replay one.
/// </para>
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

        // The SAME nominal period, read on the new definition's clock: this is the whole point of naming the
        // period rather than the instant — a schedule that moves from Rome to Kiritimati keeps the day it was
        // on, at that day's local start, instead of being carried to whatever instant its old offset made.
        var replacementZone = replacement.GoverningZone ?? TimeZoneInfo.Utc;
        var startUtc        = ToInstant(periodStart, replacementZone);
        var endUtc          = ToInstant(periodEnd, replacementZone);

        // WHERE INSIDE the period the cursor stood, counted in slots of the old definition. The first slot of
        // the period is the answer only when the cursor IS the first slot, which is every period that holds
        // exactly one; a period that holds more — OnDays(Mon, Wed).AtTimes(9, 15), EveryWeek().OnDays(Mon,Thu)
        // — would otherwise be rebased BACKWARD onto an occurrence that has already run, replaying it and
        // spending one more of MaxRuns, while RecalculateFromNow on the very same definition answers the later
        // slot. (A MONTH period never holds more than one, whatever it names: see CursorCarriesTheDay.)
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
    /// The first one comes from <see cref="RecurringTask.FirstOccurrenceOnOrAfter"/>, the one question the
    /// grid answers inclusively; the rest are ordinary successors asked with the BOUNDS IGNORED, because
    /// <see cref="WithinBound"/> is what applies <c>RunUntil</c> and it has to see the slot the cursor's
    /// position names rather than the last one before the bound.
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
    /// The walk is bounded by the period, which is what makes it affordable: it stops at the period's end.
    /// A cursor PAST every slot of its period is not one the grid produced — a row written by hand, a seeded
    /// backlog — and it stands at the last position rather than at one that does not exist, which is what
    /// keeps a period holding a single slot answering with that slot wherever the cursor sits inside it.
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
    /// Placed by hand rather than asked of the grid, for the reason in <see cref="CursorCarriesTheDay"/>. An
    /// EMPTY <c>OnTimes</c> is the one shape that constrains no time at all — the interval hands the probe's
    /// own time of day straight back — so there the cursor carries the time too and it is kept verbatim.
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
    /// in the day. Clamped for the same reason as <see cref="PositionInPeriod"/>: a cursor past every time the
    /// definition names stands at the last one.
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
    /// <c>RunUntil</c> is exclusive everywhere on the grid, and two of the three branches above place the slot
    /// BY HAND — the plain cadence keeps the cursor verbatim, the week and month cadences that carry their day
    /// on the cursor compose it from the period start — so neither ever passes through
    /// <see cref="RecurringTask.FirstOccurrenceOnOrAfter"/>, which is the only thing that applies the bound.
    /// Without this a schedule wound down with <c>RunUntil</c> — the very change <c>RequireSameShape</c>
    /// admits — would be parked at a cursor past its own end and run one more time, where
    /// <see cref="Abstractions.RescheduleMode.RecalculateFromNow"/> refuses the same definition outright.
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
    /// A week cadence with no <c>OnDays</c> steps <c>current.AddDays(7 * Interval)</c>, and EVERY month cadence
    /// steps <c>current.AddMonths(Interval)</c> and only then applies its day selector — which walks FORWARD
    /// from the day it was handed and stays there for good. Both keep the day of whatever they were handed, so
    /// the grid's phase lives on the cursor. Naming the whole week or month as the period and asking the grid
    /// for its first slot then answers from the phase the backward probe happened to land on — a Wednesday
    /// series comes back on a Sunday, and <c>OnDays(1, 15)</c> standing on the 15th comes back on the 1st,
    /// because the probe enters the month at its start and the FIRST listed day is what it finds. Every
    /// occurrence after it is computed from there. Both definitions agree on this, because
    /// <see cref="SameGrid"/> compares exactly those selectors.
    /// </remarks>
    private static bool CursorCarriesTheDay(RecurringTask definition) => definition.PeriodKind switch
    {
        // A finer interval underneath decides the day itself, so the cursor is not the only thing carrying it
        // and placing the slot by hand would be guessing. Those shapes keep the grid probe.
        SchedulePeriodKind.Week => definition.WeekInterval is { OnDays.Length: 0 } && definition.DayInterval is null,

        // A month period holds exactly ONE slot however it names its day: OnDay pins it, OnFirst computes it,
        // OnDays walks forward to the first listed day at or after the anchor's — and none of the three fires
        // twice in a month, because the cascade advances the period before it selects inside it. So the day a
        // month grid lands on is the day the cursor already stands on, whichever selector produced it.
        SchedulePeriodKind.Month => definition.MonthInterval is not null
                                    && definition.WeekInterval is null && definition.DayInterval is null,

        _ => false
    };

    /// <summary>
    /// Refuses two definitions a cursor cannot be carried between, naming which half of the shape differs.
    /// </summary>
    /// <remarks>
    /// A rebase only claims to preserve WHEN INSIDE the period an occurrence falls. Everything that decides
    /// WHICH periods have occurrences at all — the cadence, the day and month selectors, the calendar-vs-elapsed
    /// nature of the grid — must therefore be identical; what may move is the time of day, the zone, the
    /// termination bounds and the misfire caps, which is the whole set of changes an operator makes to a
    /// running schedule.
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

    /// <summary>
    /// A nominal local boundary read back as an instant in <paramref name="zone"/>.
    /// </summary>
    /// <remarks>
    /// Through the same mapping the grid itself uses, so a period boundary that a daylight-saving gap removed
    /// (midnight does not exist in Chile or Cuba on a transition night) becomes the first local tick that does,
    /// rather than throwing or silently sliding by an hour.
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

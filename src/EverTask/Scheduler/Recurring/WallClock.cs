namespace EverTask.Scheduler.Recurring;

/// <summary>
/// How a nominal wall-clock slot came back as an instant.
/// </summary>
internal enum WallMappingKind
{
    /// <summary>The wall time exists exactly once in the zone: the ordinary case.</summary>
    Exact = 0,

    /// <summary>
    /// The wall time is repeated by a DST fall-back and the FIRST pass was taken (T7) — the one with the
    /// larger offset, which is the earlier of the two instants.
    /// </summary>
    Ambiguous = 1,

    /// <summary>
    /// The wall time does not exist (a DST gap) and the slot was moved to the first local tick that does
    /// (T6). Several nominal slots inside one gap therefore land on the same instant.
    /// </summary>
    Shifted = 2
}

/// <summary>
/// The instant a nominal wall-clock slot stands for, plus what had to happen to get there.
/// </summary>
/// <param name="Utc">The instant, in UTC.</param>
/// <param name="Consumed">
/// True when the instant is at or before the one the walk started from. The caller must NOT return it: it is
/// either a slot a DST gap collapsed onto an instant already served, or the second reading of a repeated hour.
/// The walk continues from the NOMINAL wall slot, never from this instant (T7) — advancing from a shifted
/// instant would skip the slots the gap swallowed.
/// </param>
/// <param name="Kind">Which of the three cases produced <paramref name="Utc"/>.</param>
/// <param name="CollapsedCount">
/// How many further nominal slots this instant stands for — the "compressed slots" counter of T6.
/// <see cref="WallClock.ToUtc"/> is handed one slot at a time and always answers 0; the grid walk that folds
/// the consumed ones into a single occurrence (<c>RecurringTask.NextGridOccurrenceInZone</c>) reports its own
/// tally here, and that is what reaches the log.
/// </param>
internal readonly record struct WallMapping(
    DateTimeOffset Utc,
    bool Consumed,
    WallMappingKind Kind,
    int CollapsedCount);

/// <summary>
/// The two directions of the wall-clock mapping a calendar-anchored schedule needs: an instant read as a local
/// clock, and a nominal local slot read back as an instant.
/// </summary>
/// <remarks>
/// Nothing here assumes a DST shift is one hour, or that a transition lands on a minute boundary: Lord Howe
/// moves by 30 minutes, and historical transitions have landed on odd seconds. The gap search brackets and
/// bisects on ticks instead.
/// </remarks>
internal static class WallClock
{
    /// <summary>
    /// How far past a non-existent local time the search for the first valid one may go. The widest gap the
    /// tz database has ever contained is 24 hours (Pacific/Kiritimati and Pacific/Apia, when they crossed the
    /// date line), so this is double the real worst case and is only there to keep the loop finite.
    /// </summary>
    private const long MaxGapTicks = 48 * TimeSpan.TicksPerHour;

    /// <summary>The instant <paramref name="utc"/>, read on <paramref name="zone"/>'s clock.</summary>
    internal static DateTimeOffset ToWall(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone);

    /// <summary>
    /// The instant a nominal wall-clock slot stands for in <paramref name="zone"/>, with the DST rules of T6
    /// and T7 applied.
    /// </summary>
    /// <param name="wallNominal">
    /// The slot as the schedule's calendar produced it — a local date and time, with no offset attached.
    /// </param>
    /// <param name="zone">The schedule's zone.</param>
    /// <param name="notBeforeUtc">
    /// The instant the walk is standing on. A result at or before it is reported as
    /// <see cref="WallMapping.Consumed"/> rather than returned.
    /// </param>
    internal static WallMapping ToUtc(DateTime wallNominal, TimeZoneInfo zone, DateTimeOffset notBeforeUtc)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // IsInvalidTime / IsAmbiguousTime reject a UTC-kind DateTime outright, and a DateTimeOffset's DateTime
        // is already unspecified — this only guards a caller that built one by hand.
        var nominal = DateTime.SpecifyKind(wallNominal, DateTimeKind.Unspecified);

        DateTime        effective;
        TimeSpan        offset;
        WallMappingKind kind;

        if (zone.IsInvalidTime(nominal))
        {
            effective = FirstValidLocalTime(nominal, zone);
            offset    = zone.GetUtcOffset(effective);
            kind      = WallMappingKind.Shifted;
        }
        else if (zone.IsAmbiguousTime(nominal))
        {
            effective = nominal;
            offset    = LargestOffset(zone.GetAmbiguousTimeOffsets(nominal));
            kind      = WallMappingKind.Ambiguous;
        }
        else
        {
            effective = nominal;
            offset    = zone.GetUtcOffset(nominal);
            kind      = WallMappingKind.Exact;
        }

        var utc = new DateTimeOffset(effective, offset).ToUniversalTime();

        // CollapsedCount is 0 here by construction: one call sees one slot, and only the walk knows how many
        // of them ended up on this instant.
        return new WallMapping(utc, utc <= notBeforeUtc, kind, CollapsedCount: 0);
    }

    /// <summary>
    /// The first pass of a repeated hour. Instant equals wall time minus offset, so the LARGEST offset is the
    /// EARLIEST instant — which is what a calendar slot means by "at 02:30", the first time the clock says so.
    /// </summary>
    private static TimeSpan LargestOffset(TimeSpan[] offsets)
    {
        var largest = offsets[0];

        for (var i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] > largest)
                largest = offsets[i];
        }

        return largest;
    }

    /// <summary>
    /// The first local time after <paramref name="invalid"/> that the zone actually has, found by doubling a
    /// bracket until a valid probe lands and then bisecting on ticks.
    /// </summary>
    /// <remarks>
    /// A gap is one contiguous run of non-existent local times, so "invalid up to here, valid from there" is
    /// monotone across it and bisection lands on the exact boundary — whatever its width and wherever it
    /// falls. The bracket starts at one second so the first probe is already past the shortest transition on
    /// record, and doubles from there.
    /// </remarks>
    private static DateTime FirstValidLocalTime(DateTime invalid, TimeZoneInfo zone)
    {
        var headroom = (DateTime.MaxValue - invalid).Ticks;
        var ceiling  = Math.Min(MaxGapTicks, headroom);

        long invalidTicks = 0;                       // known invalid: `invalid` itself
        var  probe        = Math.Min(TimeSpan.TicksPerSecond, ceiling);

        while (probe <= 0 || zone.IsInvalidTime(invalid.AddTicks(probe)))
        {
            if (probe >= ceiling)
            {
                throw new InvalidTimeZoneException(
                    $"'{zone.Id}' has no valid local time within {MaxGapTicks / TimeSpan.TicksPerHour} hours " +
                    $"of {invalid:O}, so the schedule's slot cannot be mapped to an instant.");
            }

            invalidTicks = probe;
            probe        = Math.Min(probe * 2, ceiling);
        }

        // Invariant: invalid.AddTicks(invalidTicks) is invalid, invalid.AddTicks(probe) is valid.
        while (probe - invalidTicks > 1)
        {
            var middle = invalidTicks + (probe - invalidTicks) / 2;

            if (zone.IsInvalidTime(invalid.AddTicks(middle)))
                invalidTicks = middle;
            else
                probe = middle;
        }

        return invalid.AddTicks(probe);
    }
}

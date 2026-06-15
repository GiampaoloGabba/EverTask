namespace EverTask.Scheduler.Recurring;

public class RecurringTask
{
    public bool            RunNow          { get; set; }
    public TimeSpan?       InitialDelay    { get; set; }
    public DateTimeOffset? SpecificRunTime { get; set; }
    public CronInterval?   CronInterval    { get; set; }
    public SecondInterval? SecondInterval  { get; set; }
    public MinuteInterval? MinuteInterval  { get; set; }
    public HourInterval?   HourInterval    { get; set; }
    public DayInterval?    DayInterval     { get; set; }
    public WeekInterval?   WeekInterval    { get; set; }
    public MonthInterval?  MonthInterval   { get; set; }
    public int?            MaxRuns         { get; set; }
    public DateTimeOffset? RunUntil        { get; set; }

    //used for serialization/deserialization
    public RecurringTask() { }


    public DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun, bool isRecovery = false)
    {
        if (currentRun >= MaxRuns) return null;

        current = current.ToUniversalTime();

        if (RunUntil <= current) return null;

        // The first occurrence (RunNow / SpecificRunTime / InitialDelay) must be validated against
        // RunUntil too — only subsequent occurrences were, so a first run beyond RunUntil would fire
        // anyway (CU8).
        DateTimeOffset? FirstRunOrNull(DateTimeOffset? candidate) =>
            candidate.HasValue && RunUntil.HasValue && candidate.Value >= RunUntil.Value ? null : candidate;

        DateTimeOffset? runtime = null;

        // For first run (currentRun == 0), apply the initial run configuration — UNLESS this is a
        // recovery recompute: the first run's absolute time was already decided at dispatch (and stored
        // as NextRunUtc), so re-applying InitialDelay/RunNow/SpecificRunTime here would shift the entire
        // grid forward by the delay at every restart (L25-firstrun).
        if (currentRun == 0 && !isRecovery)
        {
            if (RunNow)
            {
                runtime = DateTimeOffset.UtcNow;
            }
            else if (SpecificRunTime.HasValue)
            {
                runtime = SpecificRunTime.Value.ToUniversalTime();
            }
            else if (InitialDelay.HasValue)
            {
                // InitialDelay always takes precedence - it defines the absolute first run time
                return FirstRunOrNull(current.Add(InitialDelay.Value));
            }
        }

        // Calculate next occurrence from the appropriate base time:
        // - If SpecificRunTime is set and in the past, calculate from SpecificRunTime to properly skip past occurrences
        // - Otherwise, calculate from current time
        var baseTime = (currentRun == 0 && runtime.HasValue && runtime.Value < current)
            ? runtime.Value
            : current;
        var next = GetNextOccurrence(baseTime);

        if (currentRun > 0) return next;

        if (next == null) return FirstRunOrNull(runtime);

        // For RunNow or SpecificRunTime, use runtime if:
        // 1. It's in the future (always use future SpecificRunTime)
        // 2. It's in the recent past (within 20 seconds) AND before next interval
        // No arbitrary gap required - the user explicitly requested this runtime
        if (runtime.HasValue)
        {
            // If runtime is in the future, always use it
            if (runtime.Value > current)
            {
                return FirstRunOrNull(runtime);
            }

            // If runtime is in the recent past, use it only if it's before next interval
            bool runtimeIsBeforeNext = runtime < next;
            bool notTooFarInPast = runtime.Value > current.AddSeconds(-20);

            if (runtimeIsBeforeNext && notTooFarInPast)
            {
                return FirstRunOrNull(runtime);
            }
        }

        return next;
    }

    /// <summary>
    /// Calculates the minimum interval for this recurring task.
    /// For cron expressions, calculates the interval between the next two occurrences.
    /// For interval-based tasks, returns the configured interval.
    /// </summary>
    /// <returns>Minimum interval between executions</returns>
    public TimeSpan GetMinimumInterval()
    {
        // Cron: calculate interval between next two occurrences
        if (CronInterval != null && !string.IsNullOrEmpty(CronInterval.CronExpression))
        {
            var now = DateTimeOffset.UtcNow;
            var first = CronInterval.GetNextOccurrence(now);
            if (!first.HasValue)
            {
                return TimeSpan.FromHours(1); // Fallback conservative
            }

            var second = CronInterval.GetNextOccurrence(first.Value);
            if (!second.HasValue)
            {
                return TimeSpan.FromHours(1); // Fallback conservative
            }

            var interval = second.Value - first.Value;
            return interval;
        }

        // Interval fields: use the most granular interval
        if (SecondInterval?.Interval > 0)
        {
            return TimeSpan.FromSeconds(SecondInterval.Interval);
        }
        if (MinuteInterval?.Interval > 0)
        {
            return TimeSpan.FromMinutes(MinuteInterval.Interval);
        }
        if (HourInterval?.Interval > 0)
        {
            return TimeSpan.FromHours(HourInterval.Interval);
        }
        if (DayInterval?.Interval > 0)
        {
            return TimeSpan.FromDays(DayInterval.Interval);
        }
        if (WeekInterval?.Interval > 0)
        {
            return TimeSpan.FromDays(7 * WeekInterval.Interval);
        }
        if (MonthInterval?.Interval > 0)
        {
            return TimeSpan.FromDays(30); // Conservative approximation
        }

        return TimeSpan.FromMinutes(5); // Safe default
    }

    private DateTimeOffset? GetNextOccurrence(DateTimeOffset current)
    {
        if (!string.IsNullOrEmpty(CronInterval?.CronExpression))
        {
            var nextCron = CronInterval.GetNextOccurrence(current);
            if (nextCron == null || RunUntil <= nextCron)
                return null;

            return nextCron;
        }

        var nextRun = MonthInterval?.GetNextOccurrence(current) ?? current;
        nextRun = WeekInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = DayInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = HourInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = MinuteInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = SecondInterval?.GetNextOccurrence(nextRun) ?? nextRun;

        if (nextRun < current.AddSeconds(1) || nextRun >= RunUntil)
            return null;

        return nextRun;
    }

    #region Skip-forward (post-downtime realignment)

    // Next-run walk: only ever runs for non-uniform (calendar) schedules, which are coarse by nature
    // (at most a few dozen occurrences/day), so this cap is never reached by a real schedule — it only
    // guarantees termination against a pathological/misbehaving interval.
    private const int MaxNextRunWalkIterations = 2_000_000;

    // Skip COUNT is logging-only (Option B: it never consumes MaxRuns), so this cap is a pure cost bound:
    // beyond it the count is under-reported but the next run is unaffected. Matches the historical cron cap.
    private const int MaxSkipCountIterations = 10_000;

    /// <summary>
    /// First real occurrence strictly after <paramref name="after"/>, anchored on the known real
    /// occurrence <paramref name="anchor"/> (with <c>anchor &lt;= after</c>). This is the single
    /// skip-forward primitive shared by every schedule kind — the calendar-aware generalisation of the
    /// cron realignment. Returns <c>null</c> if the series ends (<see cref="RunUntil"/>) before any such
    /// occurrence. The normal first-run path (<see cref="CalculateNextRun"/> / <see cref="GetNextOccurrence"/>)
    /// is the single source of truth for the occurrence grid; this method never re-derives it.
    /// </summary>
    internal DateTimeOffset? NextOccurrenceStrictlyAfter(DateTimeOffset anchor, DateTimeOffset after)
    {
        // Series already ended at `after`: any occurrence strictly after it is past RunUntil. O(1) for every
        // path, and it stops a uniform series from walking millions of steps just to discover the end (U3).
        if (RunUntil.HasValue && after >= RunUntil.Value)
            return null;

        // Cron: Cronos jumps to the occurrence strictly after an arbitrary instant in O(1).
        if (!string.IsNullOrEmpty(CronInterval?.CronExpression))
            return GetNextOccurrence(after);

        // Uniform arithmetic grid (every N seconds/minutes/hours/…, incl. pure-cadence combinations): the
        // occurrence set is exactly {anchor + k*step}. Jump there in O(1) — REQUIRED for high-frequency
        // schedules where a walk would be O(millions). The candidate is self-verified on-grid; on any
        // mismatch we fall through to the walk, so the arithmetic can never emit an off-grid value. RunUntil
        // is applied HERE (once), so the jump itself need not consult it (keeping it O(1) near end-of-series).
        if (IsUniformGrid())
        {
            var jumped = TryJumpUniformGrid(anchor, after);
            if (jumped.HasValue)
                return RunUntil.HasValue && jumped.Value >= RunUntil.Value ? null : jumped;
        }

        // Calendar / non-uniform schedules (OnDays, OnHours, Month, multi-OnTimes, combinations): always
        // coarse, so a bounded walk from the anchor is cheap and is exactly the schedule's own definition.
        // GetNextOccurrence already applies the RunUntil gate.
        var occurrence = anchor;
        for (var i = 0; i < MaxNextRunWalkIterations; i++)
        {
            var next = GetNextOccurrence(occurrence);
            if (next == null)             // RunUntil reached, or no further occurrence
                return null;
            if (next.Value > after)
                return next.Value;
            if (next.Value <= occurrence) // defensive: no forward progress
                return null;
            occurrence = next.Value;
        }

        // Cap hit (pathological — a real coarse schedule never reaches it). NEVER return a value <= after:
        // a stale past next-run would be scheduled immediately and re-fire, consuming MaxRuns (U1). Return
        // the next occurrence only if it is genuinely in the future, otherwise null (stop the series).
        var tail = GetNextOccurrence(occurrence);
        return tail.HasValue && tail.Value > after ? tail : null;
    }

    /// <summary>
    /// True iff <paramref name="occurrence"/> is still the current one to run — i.e. the NEXT occurrence
    /// after it has not yet come due at <paramref name="now"/>. Used by recovery to decide whether a
    /// just-slipped occurrence should be executed now (grace) or skipped forward. Calendar-exact: it
    /// replaces the flat <see cref="GetMinimumInterval"/> heuristic, which is wrong for OnDays/Month/Week
    /// (too narrow → drops a just-due occurrence; too wide → executes a stale, superseded one) — U4/U5.
    /// </summary>
    internal bool IsOccurrenceStillCurrent(DateTimeOffset occurrence, DateTimeOffset now)
    {
        var following = NextOccurrenceStrictlyAfter(occurrence, occurrence);
        return following == null || following.Value > now;
    }

    /// <summary>
    /// Number of occurrences missed in <c>(anchor, after]</c>, reported for LOGGING ONLY (Option B: it
    /// never consumes the <see cref="MaxRuns"/> budget). Uniform grids count in O(1) by division;
    /// calendar/cron schedules walk the real schedule, bounded.
    /// </summary>
    internal int CountMissedOccurrences(DateTimeOffset anchor, DateTimeOffset after)
    {
        // Uniform grid: O(1) division (mirrors the historical simple-interval skip count, and keeps a
        // 1-second interval over a year-long downtime O(1) instead of tens of millions of walk steps).
        if (string.IsNullOrEmpty(CronInterval?.CronExpression) && IsUniformGrid())
        {
            var nextUniform = GetNextOccurrence(anchor);
            if (nextUniform == null)
                return 1; // the anchor itself is the (only) missed occurrence

            var stepTicks = (nextUniform.Value - anchor).Ticks;
            if (stepTicks <= 0)
                return 1;

            // Clamp the horizon to RunUntil: occurrences past series end are not real and must not be
            // counted (U9 — logging-only over-report). Count [anchor, horizon] inclusive of the anchor, to
            // match the calendar/cron walk's convention below (U8 — boundary off-by-one).
            var horizon = RunUntil.HasValue && RunUntil.Value < after ? RunUntil.Value : after;
            var spanTicks = (horizon - anchor).Ticks;
            if (spanTicks < 0)
                spanTicks = 0;

            var count = spanTicks / stepTicks + 1;
            return (int)Math.Min(count, int.MaxValue);
        }

        // Calendar / cron: walk the real schedule (the anchor itself is the first missed occurrence).
        var skipped    = 1;
        var occurrence = anchor;
        for (var i = 0; i < MaxSkipCountIterations; i++)
        {
            var following = GetNextOccurrence(occurrence);
            if (following == null || following.Value > after)
                break;
            occurrence = following.Value;
            skipped++;
        }

        return skipped;
    }

    /// <summary>
    /// True iff the occurrence set is a fixed arithmetic progression <c>{base + k*step}</c>: no calendar
    /// structure (no day-of-week, no selected hours, no month interval, at most one time-of-day) and at
    /// least one positive cadence field. MULTIPLE pure-cadence fields are allowed (e.g. every-5-min-at-:30
    /// is a constant 5-minute grid): <see cref="TryJumpUniformGrid"/> self-verifies the constant step and a
    /// non-constant combination safely falls back to the walk. Calendar arrays are rejected regardless of
    /// the field's <c>Interval</c> value, because <c>OnDays</c> rides on <c>DayInterval(Interval=0)</c>
    /// (F8/U10). Conservative: when in doubt the answer is "not uniform", which is always correct (slower).
    /// </summary>
    internal bool IsUniformGrid()
    {
        if (!string.IsNullOrEmpty(CronInterval?.CronExpression)) return false;
        if (MonthInterval != null) return false;

        // Calendar structure on ANY field (independent of Interval) makes the spacing non-constant.
        if (HourInterval is { OnHours.Length: > 0 }) return false;
        if (DayInterval is { OnDays.Length: > 0 }) return false;
        if (DayInterval is { OnTimes.Length: > 1 }) return false;
        if (WeekInterval is { OnDays.Length: > 0 }) return false;
        if (WeekInterval is { OnTimes.Length: > 1 }) return false;

        // At least one real cadence. Combinations of pure-cadence fields are permitted (self-verified).
        return SecondInterval is { Interval: > 0 }
            || MinuteInterval is { Interval: > 0 }
            || HourInterval is { Interval: > 0 }
            || DayInterval is { Interval: > 0 }
            || WeekInterval is { Interval: > 0 };
    }

    /// <summary>
    /// O(1) arithmetic jump to the first grid point strictly after <paramref name="after"/>, using the
    /// EXACT local step measured from the schedule itself (<c>GetNextOccurrence(anchor) - anchor</c>) — not
    /// an approximate flat interval. All arithmetic is in integer ticks (no floating-point drift). Returns
    /// the candidate only after self-verifying it is a genuine occurrence on the grid; otherwise
    /// <c>null</c>, so a schedule that is not actually uniform safely falls back to the walk.
    /// </summary>
    private DateTimeOffset? TryJumpUniformGrid(DateTimeOffset anchor, DateTimeOffset after)
    {
        // Measure the step from the STEADY grid (first -> second), NOT from the anchor -> first gap. When the
        // anchor is an off-grid first-run point (RunNow/SpecificRunTime/InitialDelay) the first gap is
        // irregular and would be mistaken for the cadence, landing off-grid (U7). `first` is always a real
        // on-grid occurrence, so jumping from it stays grid-aligned.
        var first = GetNextOccurrence(anchor);
        if (first == null)
            return null;
        if (first.Value > after)
            return first; // the very next occurrence already lands strictly after `after`

        var second = GetNextOccurrence(first.Value);
        if (second == null)
            return null; // `first` is the last occurrence and is <= after -> nothing strictly after `after`

        var stepTicks = (second.Value - first.Value).Ticks;
        if (stepTicks <= 0)
            return null;

        var spanTicks = (after - first.Value).Ticks; // >= 0 (first <= after here)
        var jumps     = spanTicks / stepTicks;        // floor

        // Overflow-safe (U14): never let the tick arithmetic run past DateTimeOffset.MaxValue — return null
        // (no representable future occurrence) instead of throwing ArgumentOutOfRangeException.
        var maxJumps = (DateTimeOffset.MaxValue.Ticks - first.Value.Ticks) / stepTicks;
        if (jumps >= maxJumps)
            return null;
        var candidate = first.Value.AddTicks(jumps * stepTicks);

        // candidate <= after by construction; one step lands strictly past it.
        if (candidate <= after)
        {
            if (candidate.Ticks > DateTimeOffset.MaxValue.Ticks - stepTicks)
                return null;
            candidate = candidate.AddTicks(stepTicks);
        }

        // Self-verify the candidate is a genuine on-grid occurrence: the previous grid point must produce
        // EXACTLY it via the real calendar step. This rejects a non-constant combination -> bail to the walk.
        // SKIPPED at/after RunUntil, where GetNextOccurrence would null any candidate >= RunUntil and force a
        // needless walk (U3): the caller gates RunUntil once on the returned candidate.
        if (!RunUntil.HasValue || candidate < RunUntil.Value)
        {
            var previous = candidate.AddTicks(-stepTicks);
            if (GetNextOccurrence(previous) != candidate)
                return null;
        }

        return candidate;
    }

    #endregion

    #region ToString in human readable format

    public override string ToString()
    {
        var parts = new List<string>();

        if (RunNow)
            parts.Add("Run immediately");

        if (InitialDelay != null)
            parts.Add($"Start after a delay of {InitialDelay.Value}");

        if (SpecificRunTime.HasValue)
            parts.Add($"Run at {SpecificRunTime.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        if (parts.Any())
            parts.Add("then");

        if (CronInterval != null)
        {
            parts.Add("Use Cron expression:");
            parts.Add(CronInterval.CronExpression);
            return string.Join(" ", parts);
        }

        if (SecondInterval is { Interval: > 0 }) parts.Add($"every {SecondInterval.Interval} second(s)");

        if (MinuteInterval != null)
        {
            if (MinuteInterval.Interval > 0)
                parts.Add($"every {MinuteInterval.Interval} minute(s)");
            if (MinuteInterval.OnSecond != 0)
                parts.Add($"at second {MinuteInterval.OnSecond}");
        }

        if (HourInterval != null)
        {
            if (HourInterval.Interval > 0)
                parts.Add($"every {HourInterval.Interval} hour(s)");
            if (HourInterval.OnHours.Any())
                parts.Add($"at hour(s) {string.Join(" - ", HourInterval.OnHours)}");
            if (HourInterval.OnMinute != null)
                parts.Add($"at minute {HourInterval.OnMinute}");
            if (HourInterval.OnSecond != null)
                parts.Add($"at second {HourInterval.OnSecond}");
        }

        if (DayInterval != null)
        {
            if (DayInterval.Interval > 0)
                parts.Add($"every {DayInterval.Interval} day(s)");
            if (DayInterval.OnTimes.Any())
                parts.Add($"at {string.Join(" - ", DayInterval.OnTimes)}");
            if (DayInterval.OnDays.Any())
                parts.Add($"on {string.Join(" - ", DayInterval.OnDays)}");
        }

        if (WeekInterval != null)
        {
            if (WeekInterval.Interval > 0)
                parts.Add($"every {WeekInterval.Interval} week(s)");
            if (WeekInterval.OnDays.Any())
                parts.Add($"on {string.Join(" - ", WeekInterval.OnDays)}");
        }

        if (MonthInterval != null)
        {
            if (MonthInterval.Interval > 0)
                parts.Add($"every {MonthInterval.Interval} month(s)");
            if (MonthInterval.OnDay != null)
                parts.Add($"on day {MonthInterval.OnDay}");
            if (MonthInterval.OnFirst != null)
                parts.Add($"on first {MonthInterval.OnFirst}");
            if (MonthInterval.OnDays.Any())
                parts.Add($"on {string.Join(" - ", MonthInterval.OnDays)}");
            if (MonthInterval.OnTimes.Any())
                parts.Add($"at {string.Join(" - ", MonthInterval.OnTimes)}");
            if (MonthInterval.OnMonths.Any())
                parts.Add($"in {string.Join(" - ", MonthInterval.OnMonths)}");
        }

        if (RunUntil != null)
            parts.Add($"until {RunUntil.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        if (MaxRuns != null)
            parts.Add($"up to {MaxRuns} times");

        return string.Join(" ", parts);
    }

    #endregion
}

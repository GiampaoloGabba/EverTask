using System.Text.Json.Serialization;

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

    /// <summary>
    /// How this schedule produces its occurrences. <see cref="Abstractions.OccurrenceMode.Inline"/> (the
    /// default) is the legacy behaviour: the schedule row runs the handler itself.
    /// </summary>
    /// <remarks>
    /// Omitted from the JSON while it holds the default, so the serialized form of every schedule written
    /// before durable occurrences existed stays byte-identical (the EverTask serializer writes nulls and
    /// defaults by design, so the attribute — not a convention — is what preserves those bytes).
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OccurrenceMode OccurrenceMode { get; set; }

    /// <summary>
    /// The IANA id of the time zone this schedule's calendar is read on, or <c>null</c> for the legacy
    /// behaviour, where every wall-clock component means UTC (T1).
    /// </summary>
    /// <remarks>
    /// Omitted from the JSON while it is null, so the serialized form of every schedule written before zones
    /// existed stays byte-identical: the EverTask serializer writes nulls by design, so the attribute — not a
    /// convention — is what preserves those bytes. The id is stored, never the resolved
    /// <see cref="TimeZoneInfo"/>: the row outlives the process and the zone's rules change under it.
    /// Only a <see cref="ScheduleSemantics.Calendar"/> schedule may carry one; see <see cref="Validate"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeZoneId { get; set; }

    private TimeZoneInfo? _zone;
    private string?       _zoneId;

    //used for serialization/deserialization
    public RecurringTask() { }

    /// <summary>
    /// What this schedule's grid is anchored to, and therefore whether <see cref="TimeZoneId"/> can govern it.
    /// Derived from the definition, never stored (T5).
    /// </summary>
    [JsonIgnore]
    public ScheduleSemantics Semantics => IsCalendarAnchored()
                                              ? ScheduleSemantics.Calendar
                                              : ScheduleSemantics.Elapsed;

    /// <summary>
    /// The resolved <see cref="TimeZoneId"/>, cached, or null when the schedule carries none.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The stored id no longer resolves on this machine. <see cref="Validate"/> runs on every path that
    /// accepts a schedule, so this only fires for a definition that bypassed it.
    /// </exception>
    internal TimeZoneInfo? Zone
    {
        get
        {
            var id = TimeZoneId;

            if (id is null)
                return null;

            if (_zone is not null && _zoneId == id)
                return _zone;

            _zone  = ScheduleTimeZone.Resolve(id);
            _zoneId = id;

            return _zone;
        }
    }

    /// <summary>
    /// The zone that actually moves this schedule's occurrences: null for an
    /// <see cref="ScheduleSemantics.Elapsed"/> grid, for a schedule with no zone, and for plain UTC — all
    /// three are exactly what the legacy arithmetic already computes, so they keep taking that path (T5/T8).
    /// </summary>
    internal TimeZoneInfo? GoverningZone =>
        TimeZoneId is null || TimeZoneId == ScheduleTimeZone.UtcId || Semantics != ScheduleSemantics.Calendar
            ? null
            : Zone;

    /// <summary>
    /// <paramref name="utcInstant"/> read on the schedule's own clock, offset included so the two passes of a
    /// DST fall-back are distinguishable (T13). Null when the schedule carries no zone, and null rather than
    /// a throw when the id no longer resolves: a delivery must not fail over what it reports about itself.
    /// </summary>
    internal DateTimeOffset? ToScheduleLocalTime(DateTimeOffset? utcInstant) =>
        utcInstant is { } instant && ScheduleTimeZone.TryResolve(TimeZoneId, out var zone)
            ? WallClock.ToWall(instant, zone)
            : null;

    /// <summary>
    /// Stores <paramref name="timeZone"/> as the id that will bring it back on the next run (T2). The one
    /// place the builders' <c>InTimeZone</c> goes through, so an id that would not resolve later is refused
    /// now, before it reaches a row.
    /// </summary>
    internal void SetTimeZone(TimeZoneInfo timeZone) => TimeZoneId = ScheduleTimeZone.Normalize(timeZone);

    /// <inheritdoc cref="SetTimeZone(TimeZoneInfo)"/>
    internal void SetTimeZone(string timeZoneId) => TimeZoneId = ScheduleTimeZone.Normalize(timeZoneId);

    /// <summary>
    /// True when the grid is anchored to a wall clock or a calendar rather than being a constant step in
    /// elapsed time (T5). Day, week and month intervals always are: each of them snaps its result to a time
    /// of day, and a time of day only means something on some clock.
    /// </summary>
    private bool IsCalendarAnchored() =>
        !string.IsNullOrEmpty(CronInterval?.CronExpression)
     || MonthInterval != null
     || WeekInterval != null
     || DayInterval != null
     || HourInterval is { OnHours.Length: > 0 };

    /// <summary>
    /// Validates every interval present on this schedule, throwing on corrupt-but-deserializable metadata: an
    /// unparseable cron, an out-of-range OnDays/OnHours/OnMonths selector, a negative Interval, or an
    /// <see cref="Abstractions.OccurrenceMode"/> outside the defined values. Invoked right
    /// after a recovery deserialize so corrupt schedule metadata is routed to the TERMINAL poison path (B1)
    /// instead of throwing downstream at next-run (a bounded per-restart failure) or producing a wrong schedule.
    /// </summary>
    public void Validate()
    {
        // The tolerant enum converter maps an unknown numeric value through verbatim rather than failing the
        // whole payload; enforcing the defined set is this method's job (B2). Without it an out-of-range mode
        // is not Durable, so the row silently degrades to the inline path and the schedule row runs the
        // handler itself — the wrong semantics, where every other corrupt schedule value is poisoned.
        // The generic overload, not the Type-based one: Validate runs on every recurring dispatch, and the
        // non-generic form boxes the value and walks the enum's names through reflection.
        if (!Enum.IsDefined(OccurrenceMode))
            throw new ArgumentException(
                $"Invalid OccurrenceMode '{(int)OccurrenceMode}': not a defined value.", nameof(OccurrenceMode));

        CronInterval?.Validate();
        SecondInterval?.Validate();
        MinuteInterval?.Validate();
        HourInterval?.Validate();
        DayInterval?.Validate();
        WeekInterval?.Validate();
        MonthInterval?.Validate();

        ValidateTimeZone();
    }

    /// <summary>
    /// T10: the zone is checked on every path that accepts a schedule — the fluent build, a definition handed
    /// to the dispatcher directly, and a recovery deserialize. An id this machine cannot resolve is corrupt
    /// schedule metadata like an unparseable cron, and takes the same terminal poison route.
    /// </summary>
    /// <remarks>
    /// It is also where the id is CANONICALIZED to its IANA spelling (T2), because this is the one gate all
    /// those paths share: the fluent builders normalize in <c>InTimeZone</c>, but a definition handed straight
    /// to the public <c>ExecuteDispatch</c> never meets them, and would be serialized into the row with
    /// whatever spelling the caller used — a Windows id that resolves to nothing on a Linux replica of the
    /// same deployment. Validation runs before the schedule is serialized on every one of those paths, so the
    /// row gets the canonical form regardless of how the definition was built.
    /// <para>
    /// A zone on an <see cref="ScheduleSemantics.Elapsed"/> schedule is refused rather than ignored: the grid
    /// is the same set of instants in every zone, so accepting the call would promise something the schedule
    /// cannot deliver. The check lives here and not in <c>InTimeZone</c> because the builder chain has no
    /// final shape yet — <c>Schedule().InTimeZone(z).EveryDay()</c> is calendar-anchored by the time it is
    /// built, and only the built definition knows that.
    /// </para>
    /// </remarks>
    private void ValidateTimeZone()
    {
        if (TimeZoneId is null)
            return;

        TimeZoneId = ScheduleTimeZone.Normalize(TimeZoneId);

        if (Semantics == ScheduleSemantics.Elapsed)
        {
            throw new InvalidOperationException(
                $"The time zone '{TimeZoneId}' has no effect on this schedule: a plain cadence " +
                "(every N seconds/minutes/hours) is a constant step in elapsed time and produces the same " +
                "instants in every zone. Anchor the schedule to a calendar — a time of day, a day of the " +
                "week, a month selector or a cron expression — or drop the time zone.");
        }
    }


    /// <summary>
    /// The historical signature, kept BYTE-FOR-BYTE so an assembly compiled against the previous release
    /// still binds (P6/X6): appending an optional parameter would have replaced this method's IL signature
    /// and greeted every such consumer with a <c>MissingMethodException</c>. It resolves the clock itself.
    /// </summary>
    public DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun, bool isRecovery = false) =>
        CalculateNextRun(current, currentRun, isRecovery, null);

    /// <param name="nowUtc">
    /// The scheduling clock's "now", used only by the <see cref="RunNow"/> first-run branch. Null falls back
    /// to the real clock, for callers outside the deterministic scheduling path.
    /// </param>
    public DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun, bool isRecovery,
                                            DateTimeOffset? nowUtc) =>
        CalculateNextRun(current, currentRun, isRecovery, nowUtc, out _);

    /// <inheritdoc cref="CalculateNextRun(DateTimeOffset,int,bool,DateTimeOffset?)"/>
    /// <param name="collapsedSlots">
    /// How many nominal wall-clock slots a daylight-saving transition folded into the returned occurrence
    /// (T6). Internal: it travels out to the worker on <see cref="NextRunResult.CollapsedSlotCount"/>, which
    /// is where a consumer reads it.
    /// </param>
    internal DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun, bool isRecovery,
                                              DateTimeOffset? nowUtc, out int collapsedSlots)
    {
        collapsedSlots = 0;

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
                runtime = nowUtc ?? DateTimeOffset.UtcNow;
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
        var next = GetNextOccurrence(baseTime, out var gridCollapsed);

        // The first-run branches below can answer with `runtime` instead of the grid's slot. A collapse count
        // belonging to a slot nobody ends up scheduling would be a lie, so only the paths that really return
        // `next` report one.
        if (currentRun > 0)
        {
            collapsedSlots = gridCollapsed;
            return next;
        }

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
            var runtimeIsBeforeNext = runtime < next;
            var notTooFarInPast = runtime.Value > current.AddSeconds(-20);

            if (runtimeIsBeforeNext && notTooFarInPast)
            {
                return FirstRunOrNull(runtime);
            }
        }

        collapsedSlots = gridCollapsed;
        return next;
    }

    /// <summary>
    /// Calculates the minimum interval for this recurring task.
    /// For cron expressions, calculates the interval between the next two occurrences.
    /// For interval-based tasks, returns the configured interval.
    /// </summary>
    /// <returns>Minimum interval between executions</returns>
    /// <remarks>
    /// Kept as its own zero-argument method rather than an optional parameter on the overload below: the
    /// original IL signature is what an assembly compiled against the previous release calls (P6/X6).
    /// </remarks>
    public TimeSpan GetMinimumInterval() => GetMinimumInterval(null);

    /// <summary>
    /// <see cref="GetMinimumInterval()"/> anchored on the scheduling clock.
    /// </summary>
    /// <param name="nowUtc">
    /// The scheduling clock's "now" used as the cron probe anchor. Null falls back to the real clock.
    /// </param>
    public TimeSpan GetMinimumInterval(DateTimeOffset? nowUtc)
    {
        // Cron: calculate interval between next two occurrences
        if (CronInterval != null && !string.IsNullOrEmpty(CronInterval.CronExpression))
        {
            var now  = nowUtc ?? DateTimeOffset.UtcNow;
            var zone = GoverningZone ?? TimeZoneInfo.Utc;

            var first = CronInterval.GetNextOccurrence(now, zone);
            if (!first.HasValue)
            {
                return TimeSpan.FromHours(1); // Fallback conservative
            }

            var second = CronInterval.GetNextOccurrence(first.Value, zone);
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

    /// <summary>
    /// How many nominal slots the zone walk may consume before giving up. Every iteration but the last is a
    /// slot a DST transition swallowed, so one transition costs as many iterations as it has slots — a
    /// per-minute calendar schedule inside a one-hour gap costs 60. The bound only guarantees termination
    /// against a pathological definition; a real schedule never approaches it.
    /// </summary>
    private const int MaxWallMappingIterations = 100_000;

    private DateTimeOffset? GetNextOccurrence(DateTimeOffset current) => GetNextOccurrence(current, out _);

    /// <param name="collapsedSlots">
    /// How many nominal wall-clock slots a daylight-saving transition folded into the returned occurrence
    /// (T6). Always 0 for a schedule the zone does not govern and for cron, whose transition rules are
    /// Cronos's own.
    /// </param>
    private DateTimeOffset? GetNextOccurrence(DateTimeOffset current, out int collapsedSlots)
    {
        collapsedSlots = 0;

        var zone = GoverningZone;

        if (!string.IsNullOrEmpty(CronInterval?.CronExpression))
        {
            // Cronos owns the DST rules for a cron expression, and T9 makes it the oracle the fluent API is
            // measured against — so the zone is handed to it rather than reimplemented around it.
            var nextCron = CronInterval.GetNextOccurrence(current, zone ?? TimeZoneInfo.Utc);
            if (nextCron == null || RunUntil <= nextCron)
                return null;

            return nextCron;
        }

        if (zone == null)
            return NextGridOccurrenceUtc(current);

        if (NextGridOccurrenceInZone(current, zone) is not { } mapping)
            return null;

        collapsedSlots = mapping.CollapsedCount;
        return mapping.Utc;
    }

    /// <summary>
    /// The legacy grid: the interval cascade applied straight to UTC components.
    /// </summary>
    private DateTimeOffset? NextGridOccurrenceUtc(DateTimeOffset current)
    {
        var nextRun = RunIntervalCascade(current);

        if (nextRun < current.AddSeconds(1) || nextRun >= RunUntil)
            return null;

        return nextRun;
    }

    /// <summary>
    /// The same grid, walked on <paramref name="zone"/>'s clock: the cascade reads and writes local
    /// components, and the nominal slot it lands on is mapped back to an instant with the DST rules of T6/T7.
    /// </summary>
    /// <remarks>
    /// The loop exists for the one case a single mapping cannot answer: a slot whose instant is at or before
    /// the one we started from — a nominal time a DST gap collapsed onto an instant already served, or the
    /// second reading of a repeated hour. It advances from the NOMINAL wall slot, never from the shifted
    /// instant, so several nominal slots inside one gap produce exactly one occurrence (T6) and the repeated
    /// hour fires once, on its first pass (T7).
    /// <para>
    /// Every slot that produces no occurrence of its own is counted into
    /// <see cref="WallMapping.CollapsedCount"/>, which is what the worker logs when a transition compresses a
    /// schedule (T6). They arrive two ways: the loop's own discards, and — for a slot a gap moved — the later
    /// slots up to the gap's exit, which the walk would never see because it resumes AT the exit, past them.
    /// </para>
    /// </remarks>
    private WallMapping? NextGridOccurrenceInZone(DateTimeOffset current, TimeZoneInfo zone)
    {
        var wall      = WallClock.ToWall(current, zone);
        var collapsed = 0;

        for (var i = 0; i < MaxWallMappingIterations; i++)
        {
            var nextWall = RunIntervalCascade(wall);

            // The legacy "did the grid move forward" guard, in wall terms: both sides carry the offset the
            // cascade itself worked with, so the comparison is the same one it would have made in UTC.
            if (nextWall < wall.AddSeconds(1))
                return null;

            var mapping = WallClock.ToUtc(nextWall.DateTime, zone, current);

            if (!mapping.Consumed)
            {
                if (mapping.Utc >= RunUntil)
                    return null;

                if (mapping.Kind == WallMappingKind.Shifted)
                    collapsed += CountSlotsSwallowedByGap(nextWall, WallClock.ToWall(mapping.Utc, zone));

                return mapping with { CollapsedCount = collapsed };
            }

            collapsed++;
            wall = nextWall;
        }

        return null;
    }

    /// <summary>
    /// How many further slots of this grid fall between <paramref name="gapSlot"/> — a nominal slot a DST gap
    /// removed — and <paramref name="gapExit"/>, the local time it was pushed out to. Each of them maps to the
    /// instant the walk is about to return, so each is one more slot this single occurrence answers for.
    /// </summary>
    /// <remarks>
    /// Reached only for a slot that really fell inside a gap, which for a given schedule happens on at most
    /// one occurrence per transition.
    /// </remarks>
    private int CountSlotsSwallowedByGap(DateTimeOffset gapSlot, DateTimeOffset gapExit)
    {
        var swallowed = 0;
        var probe     = gapSlot;

        for (var i = 0; i < MaxWallMappingIterations; i++)
        {
            var next = RunIntervalCascade(probe);

            // Wall components, not instants: `probe` still carries the offset from before the transition and
            // `gapExit` the one from after it, so comparing them as moments would compare two different clocks.
            if (next < probe.AddSeconds(1) || next.DateTime > gapExit.DateTime)
                return swallowed;

            swallowed++;
            probe = next;
        }

        return swallowed;
    }

    /// <summary>
    /// The interval cascade, Month → Week → Day → Hour → Minute → Second, each one refining the previous.
    /// Purely component arithmetic, which is what lets the same code answer in UTC and on a local clock.
    /// </summary>
    private DateTimeOffset RunIntervalCascade(DateTimeOffset from)
    {
        var nextRun = MonthInterval?.GetNextOccurrence(from) ?? from;
        nextRun = WeekInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = DayInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = HourInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = MinuteInterval?.GetNextOccurrence(nextRun) ?? nextRun;
        nextRun = SecondInterval?.GetNextOccurrence(nextRun) ?? nextRun;

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
    /// The NATURAL successor of <paramref name="occurrence"/> on the occurrence grid, computed while
    /// IGNORING the termination bounds (<see cref="RunUntil"/> / <see cref="MaxRuns"/>). Returns
    /// <c>null</c> only when the grid itself cannot produce one.
    /// </summary>
    /// <remarks>
    /// This is what the recovery grace window must ask. <see cref="IsOccurrenceStillCurrent"/> reads
    /// "no successor" as "still current forever", but the bounded successor is also null once the series
    /// ends — so a slot months old would be executed at restart. Asking the unbounded grid separates the
    /// two: a successor still in the future means the stored slot is genuinely the current one (a grace
    /// window as wide as one period, be it a minute or a month), a successor already past means the slot
    /// is stale and the series must be finalized instead.
    /// </remarks>
    internal DateTimeOffset? NextGridOccurrenceAfter(DateTimeOffset occurrence)
    {
        // Shallow copy: the interval objects are only read while walking the grid, and clearing the bounds
        // on the copy is what makes the walk unbounded without mutating the live definition.
        var unbounded = (RecurringTask)MemberwiseClone();
        unbounded.RunUntil = null;
        unbounded.MaxRuns  = null;

        return unbounded.NextOccurrenceStrictlyAfter(occurrence, occurrence);
    }

    /// <summary>
    /// Number of occurrences missed in <c>(anchor, after]</c>, reported for LOGGING ONLY (Option B: it
    /// never consumes the <see cref="MaxRuns"/> budget). Uniform grids count in O(1) by division;
    /// calendar/cron schedules walk the real schedule, bounded.
    /// </summary>
    /// <param name="cap">
    /// Upper bound on the returned count: the walk stops at <c>cap + 1</c> and the arithmetic result is
    /// clamped there, so a one-second grid over a long window never enumerates millions of slots just to
    /// report a number. The default keeps the historical unbounded count.
    /// </param>
    internal int CountMissedOccurrences(DateTimeOffset anchor, DateTimeOffset after, int cap = int.MaxValue)
    {
        // cap + 1 in long arithmetic: cap == int.MaxValue must not wrap to a negative ceiling.
        var ceiling = Math.Min((long)cap + 1, int.MaxValue);

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
            return (int)Math.Min(count, ceiling);
        }

        // Calendar / cron: walk the real schedule (the anchor itself is the first missed occurrence).
        var skipped    = 1;
        var occurrence = anchor;
        for (var i = 0; i < MaxSkipCountIterations && skipped < ceiling; i++)
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
        // T8: a calendar schedule read on a real clock has no constant step — local midnight is 24 hours
        // after the previous one on every day but the two the zone changes offset on. The walk is the only
        // answer there. Elapsed grids and plain-UTC calendars keep the arithmetic they always had.
        if (GoverningZone != null) return false;

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

        if (parts.Count > 0)
            parts.Add("then");

        if (CronInterval != null)
        {
            parts.Add("Use Cron expression:");
            parts.Add(CronInterval.CronExpression);
            AppendTimeZone(parts);
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
            if (HourInterval.OnHours.Length != 0)
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
            if (DayInterval.OnTimes.Length != 0)
                parts.Add($"at {string.Join(" - ", DayInterval.OnTimes)}");
            if (DayInterval.OnDays.Length != 0)
                parts.Add($"on {string.Join(" - ", DayInterval.OnDays)}");
        }

        if (WeekInterval != null)
        {
            if (WeekInterval.Interval > 0)
                parts.Add($"every {WeekInterval.Interval} week(s)");
            if (WeekInterval.OnDays.Length != 0)
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
            if (MonthInterval.OnDays.Length != 0)
                parts.Add($"on {string.Join(" - ", MonthInterval.OnDays)}");
            if (MonthInterval.OnTimes.Length != 0)
                parts.Add($"at {string.Join(" - ", MonthInterval.OnTimes)}");
            if (MonthInterval.OnMonths.Length != 0)
                parts.Add($"in {string.Join(" - ", MonthInterval.OnMonths)}");
        }

        if (RunUntil != null)
            parts.Add($"until {RunUntil.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        if (MaxRuns != null)
            parts.Add($"up to {MaxRuns} times");

        AppendTimeZone(parts);

        return string.Join(" ", parts);
    }

    /// <summary>
    /// T13: the zone is part of what the schedule MEANS, so the human-readable form — which is also what the
    /// row's <c>RecurringInfo</c> column and the dashboard show — names it. Absent for a schedule without one,
    /// which keeps every description written before zones existed unchanged.
    /// </summary>
    private void AppendTimeZone(List<string> parts)
    {
        if (TimeZoneId != null)
            parts.Add($"({TimeZoneId})");
    }

    #endregion
}

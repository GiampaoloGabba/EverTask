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

    /// <summary>The fixed moments removed from this schedule's recurring grid, or <c>null</c> for none.</summary>
    /// <remarks>Omitted while null so definitions written before exclusions remain byte-identical.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScheduleExclusions? Exclusions { get; set; }

    /// <summary>
    /// How this schedule produces its occurrences. <see cref="Abstractions.OccurrenceMode.Inline"/> (the
    /// default) is the legacy behaviour: the schedule row runs the handler itself.
    /// </summary>
    /// <remarks>
    /// Omitted from the JSON while it holds the default, so schedules written before durable occurrences
    /// existed stay byte-identical: the EverTask serializer writes nulls and defaults by design, so the
    /// attribute and not a convention is what preserves those bytes.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OccurrenceMode OccurrenceMode { get; set; }

    /// <summary>
    /// The IANA id of the time zone this schedule's calendar and calendar exclusions are read on, or
    /// <c>null</c> for the legacy behaviour, where every wall-clock component means UTC.
    /// </summary>
    /// <remarks>
    /// Omitted from the JSON while it is null, so schedules written before zones existed stay byte-identical.
    /// The id is stored, never the resolved <see cref="TimeZoneInfo"/>: the row outlives the process and the
    /// zone's rules change under it. An elapsed schedule may carry one only when day/date exclusions need an
    /// exclusion clock; see <see cref="Validate"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// What this schedule does with a slot that came due while nothing was there to run it, or <c>null</c> for
    /// the legacy behaviour (skip).
    /// </summary>
    /// <remarks>
    /// Omitted from the JSON while it is null, so the serialized form of every schedule written before misfire
    /// policies existed stays byte-identical. A policy that replays missed work implies
    /// <see cref="Abstractions.OccurrenceMode.Durable"/>; see <see cref="Validate"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MisfireSettings? Misfire { get; set; }

    /// <summary>
    /// Where a durable schedule's cursor starts, when it should not start at the first occurrence after the
    /// dispatch: the first occurrence on or after this instant, inclusive.
    /// </summary>
    /// <remarks>
    /// Omitted from the JSON while it is null, for the same byte-parity reason as the members above. Only a
    /// durable schedule may carry one — an inline schedule has nowhere to put the replayed occurrences.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? BackfillFromUtc { get; set; }

    /// <summary>
    /// The registered <see cref="INextOccurrenceProvider"/> this schedule takes its occurrences from, or
    /// <c>null</c> for the built-in grid.
    /// </summary>
    /// <remarks>
    /// Omitted from the JSON while it is null, for the same byte-parity reason as the members above. It is
    /// EXCLUSIVE with every built-in interval and with cron: a provider does not refine a grid, it replaces
    /// one. Only the key and the opaque config travel with the row — never a type name, which a rename would
    /// orphan.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderSettings? Provider { get; set; }

    private TimeZoneInfo? _zone;
    private string?       _zoneId;

    //used for serialization/deserialization
    public RecurringTask() { }

    /// <summary>
    /// What this schedule's grid is anchored to, and therefore whether <see cref="TimeZoneId"/> can govern it.
    /// Derived from the definition, never stored.
    /// </summary>
    [JsonIgnore]
    public ScheduleSemantics Semantics => IsCalendarAnchored()
                                              ? ScheduleSemantics.Calendar
                                              : ScheduleSemantics.Elapsed;

    /// <summary>
    /// The calendar unit one occurrence of this schedule belongs to: what a rebased cursor has to stay inside.
    /// Derived from the definition, never stored.
    /// </summary>
    /// <remarks>
    /// The DOMINANT selector decides, coarsest first, since that is the unit inside which the finer ones only
    /// choose a position. Cron and a provider are <see cref="SchedulePeriodKind.None"/>: neither states a
    /// period anything outside it could rely on.
    /// </remarks>
    [JsonIgnore]
    internal SchedulePeriodKind PeriodKind =>
        Provider != null                                     ? SchedulePeriodKind.None
        : !string.IsNullOrEmpty(CronInterval?.CronExpression) ? SchedulePeriodKind.None
        : MonthInterval != null                               ? SchedulePeriodKind.Month
        : WeekInterval != null                                ? SchedulePeriodKind.Week
        : DayInterval != null                                 ? SchedulePeriodKind.Day
        : HourInterval is { OnHours.Length: > 0 }             ? SchedulePeriodKind.Day
                                                              : SchedulePeriodKind.Instant;

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
    /// three are exactly what the legacy arithmetic already computes, so they keep taking that path.
    /// </summary>
    internal TimeZoneInfo? GoverningZone =>
        TimeZoneId is null || TimeZoneId == ScheduleTimeZone.UtcId || Semantics != ScheduleSemantics.Calendar
            ? null
            : Zone;

    /// <summary>
    /// <paramref name="utcInstant"/> read on the schedule's own clock, offset included so the two passes of a
    /// DST fall-back are distinguishable. Null when the schedule carries no zone, and null rather than a
    /// throw when the id no longer resolves: a delivery must not fail over what it reports about itself.
    /// </summary>
    internal DateTimeOffset? ToScheduleLocalTime(DateTimeOffset? utcInstant) =>
        ScheduleTimeZone.ToLocalTime(TimeZoneId, utcInstant);

    /// <summary>
    /// Stores <paramref name="timeZone"/> as the id that will bring it back on the next run. The one place
    /// the builders' <c>InTimeZone</c> goes through, so an id that would not resolve later is refused now,
    /// before it reaches a row.
    /// </summary>
    internal void SetTimeZone(TimeZoneInfo timeZone) => TimeZoneId = ScheduleTimeZone.Normalize(timeZone);

    /// <inheritdoc cref="SetTimeZone(TimeZoneInfo)"/>
    internal void SetTimeZone(string timeZoneId) => TimeZoneId = ScheduleTimeZone.Normalize(timeZoneId);

    /// <summary>
    /// True when the grid is anchored to a wall clock or a calendar rather than being a constant step in
    /// elapsed time. Day, week and month intervals always are: each snaps its result to a time of day, and a
    /// time of day only means something on some clock. So is a provider, which is handed the zone id to read
    /// its own calendar on.
    /// </summary>
    private bool IsCalendarAnchored() =>
        Provider != null
     || !string.IsNullOrEmpty(CronInterval?.CronExpression)
     || MonthInterval != null
     || WeekInterval != null
     || DayInterval != null
     || HourInterval is { OnHours.Length: > 0 };

    /// <summary>
    /// Validates every interval present on this schedule, throwing on corrupt-but-deserializable metadata: an
    /// unparseable cron, an out-of-range OnDays/OnHours/OnMonths selector, a negative Interval, or an
    /// <see cref="Abstractions.OccurrenceMode"/> outside the defined values. Invoked right after a recovery
    /// deserialize, so corrupt schedule metadata takes the terminal poison path instead of throwing
    /// downstream at next-run or producing a wrong schedule.
    /// </summary>
    public void Validate() => Validate(null);

    /// <inheritdoc cref="Validate()"/>
    /// <param name="context">
    /// The host's provider and exclusion-calendar registries, when the caller can reach them. Persisted names
    /// are then resolved here so ingress can refuse an unknown name before writing anything. Null preserves
    /// registry-free validation for definitions inspected outside a configured host.
    /// </param>
    internal void Validate(ScheduleValidationContext? context)
    {
        // The tolerant enum converter maps an unknown numeric value through verbatim, so enforcing the
        // defined set is this method's job: an out-of-range mode is not Durable, and the row would silently
        // degrade to the inline path where every other corrupt schedule value is poisoned. The generic
        // overload, since the Type-based one boxes and walks the enum's names through reflection.
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

        ValidateExclusions(context?.Calendars);
        ValidateProvider(context?.Providers);
        ValidateTimeZone();
        ValidateMisfire();
    }

    private void ValidateExclusions(ScheduleCalendarRegistry? calendars)
    {
        if (Exclusions is not { } exclusions)
            return;

        if (ScheduleExclusionNormalizer.Normalize(exclusions))
        {
            Exclusions = null;
            return;
        }

        if (exclusions.Calendars.Length > 0 && calendars != null)
            _ = calendars.Resolve(exclusions);
    }

    /// <summary>
    /// A provider REPLACES the grid, so it cannot sit beside one, and the key it names has to resolve.
    /// </summary>
    /// <remarks>
    /// Exclusivity is refused rather than resolved by precedence: cron already wins silently over every
    /// interval, and a second silent winner would make a schedule naming both mean something nobody wrote.
    /// </remarks>
    private void ValidateProvider(Occurrences.OccurrenceProviderRegistry? providers)
    {
        if (Provider is not { } provider)
            return;

        provider.Validate();

        if (Exclusions != null)
        {
            throw new InvalidOperationException(
                $"The schedule takes its occurrences from the provider '{provider.Key}' AND declares fixed " +
                "exclusions. A provider owns its calendar and must apply its own exclusions.");
        }

        if (!string.IsNullOrEmpty(CronInterval?.CronExpression) || SecondInterval != null || MinuteInterval != null
         || HourInterval != null || DayInterval != null || WeekInterval != null || MonthInterval != null)
        {
            throw new InvalidOperationException(
                $"The schedule takes its occurrences from the provider '{provider.Key}' AND names a built-in " +
                "interval or cron expression. A provider replaces the grid instead of refining it: use one or " +
                "the other.");
        }

        if (providers != null && !providers.IsRegistered(provider.Key))
            throw providers.UnknownKey(provider.Key);
    }

    /// <summary>
    /// True when every due slot of this schedule becomes its own durable row instead of the schedule row
    /// running the handler itself.
    /// </summary>
    internal bool IsDurable => OccurrenceMode == OccurrenceMode.Durable;

    /// <summary>The policy in force, with the legacy default spelled out for a schedule that declared none.</summary>
    internal MisfirePolicy MisfirePolicy => Misfire?.Policy ?? Abstractions.MisfirePolicy.Skip;

    /// <summary>
    /// The two rules that tie a misfire policy to an occurrence mode: a policy that REPLAYS missed work needs
    /// somewhere durable to put the replay, and an inline schedule has nowhere.
    /// </summary>
    /// <remarks>
    /// Refused rather than promoted silently. The fluent builders set both together, so this only fires for a
    /// hand-built or hand-edited definition, and for those, quietly turning an inline schedule durable would
    /// change where its executions live.
    /// </remarks>
    private void ValidateMisfire()
    {
        Misfire?.Validate();

        if (MisfirePolicy != Abstractions.MisfirePolicy.Skip && !IsDurable)
        {
            throw new InvalidOperationException(
                $"The misfire policy '{MisfirePolicy}' replays missed occurrences, which needs durable " +
                "occurrences: every replayed slot becomes its own row. Call WithDurableOccurrences(), or set " +
                "OccurrenceMode to Durable on a hand-built definition.");
        }

        if (BackfillFromUtc != null && !IsDurable)
        {
            throw new InvalidOperationException(
                "BackfillFrom only applies to a durable schedule: an inline one runs the handler from the " +
                "schedule row itself and has nowhere to put the backfilled occurrences.");
        }
    }

    /// <summary>
    /// The zone is checked on every path that accepts a schedule — the fluent build, a definition handed to
    /// the dispatcher directly, and a recovery deserialize. An id this machine cannot resolve is corrupt
    /// schedule metadata like an unparseable cron, and takes the same terminal poison route.
    /// </summary>
    /// <remarks>
    /// Also where the id is CANONICALIZED to its IANA spelling, this being the one gate all those paths
    /// share: a definition handed straight to the public <c>ExecuteDispatch</c> never meets a builder, and
    /// would reach the row with a Windows id that resolves to nothing on a Linux replica. A zone on an
    /// <see cref="ScheduleSemantics.Elapsed"/> schedule is refused unless day/date exclusions read their
    /// calendar on it; the check lives here and not in <c>InTimeZone</c> because only the built definition
    /// knows the chain's final shape.
    /// </remarks>
    private void ValidateTimeZone()
    {
        if (TimeZoneId is null)
            return;

        TimeZoneId = ScheduleTimeZone.Normalize(TimeZoneId);

        if (Semantics == ScheduleSemantics.Elapsed && !HasCalendarExclusions())
        {
            throw new InvalidOperationException(
                $"The time zone '{TimeZoneId}' has no effect on this schedule: a plain cadence " +
                "(every N seconds/minutes/hours) is a constant step in elapsed time and produces the same " +
                "instants in every zone. Anchor the schedule to a calendar, add a day/date exclusion that " +
                "needs an exclusion clock, or drop the time zone.");
        }
    }

    internal bool HasCalendarExclusions() =>
        Exclusions is { } exclusions &&
        ((exclusions.Days?.Length ?? 0) > 0 || (exclusions.Dates?.Length ?? 0) > 0 ||
         (exclusions.Calendars?.Length ?? 0) > 0);


    /// <summary>
    /// The historical signature, kept byte-for-byte so an assembly compiled against the previous release
    /// still binds: appending an optional parameter replaces the IL signature and greets every such consumer
    /// with a <c>MissingMethodException</c>. It resolves the clock itself.
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
    /// How many nominal wall-clock slots a daylight-saving transition folded into the returned occurrence.
    /// Internal: it travels out to the worker on <see cref="NextRunResult.CollapsedSlotCount"/>, which is
    /// where a consumer reads it.
    /// </param>
    internal DateTimeOffset? CalculateNextRun(DateTimeOffset current, int currentRun, bool isRecovery,
                                              DateTimeOffset? nowUtc, out int collapsedSlots)
    {
        collapsedSlots = 0;

        var plan = PlanNextRun(current, currentRun, isRecovery, nowUtc);

        if (plan.IsFinal)
            return plan.Answer;

        var next = GetNextOccurrence(plan.BaseTime, out var gridCollapsed);

        return SelectNextRun(plan, next, gridCollapsed, out collapsedSlots);
    }

    /// <summary>
    /// Everything <see cref="CalculateNextRun(DateTimeOffset,int,bool,DateTimeOffset?,out int)"/> decides
    /// BEFORE it asks the grid: the termination bounds, the first-run configuration, and which instant the
    /// grid is then asked about.
    /// </summary>
    /// <remarks>
    /// Split out so a grid that answers ASYNCHRONOUSLY — a schedule whose occurrences come from an
    /// <see cref="INextOccurrenceProvider"/> — reuses this decision instead of restating it. The two halves
    /// meet again in <see cref="SelectNextRun"/>; nothing between them touches the grid.
    /// </remarks>
    internal NextRunPlan PlanNextRun(DateTimeOffset current, int currentRun, bool isRecovery, DateTimeOffset? nowUtc)
    {
        if (currentRun >= MaxRuns) return NextRunPlan.Final(null);

        current = current.ToUniversalTime();

        if (RunUntil <= current) return NextRunPlan.Final(null);

        DateTimeOffset? runtime = null;

        // The initial run configuration applies to the first run only, and NOT on a recovery recompute: the
        // first run's absolute time was decided at dispatch and stored as NextRunUtc, so re-applying
        // InitialDelay/RunNow/SpecificRunTime here shifts the whole grid forward at every restart.
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
                return NextRunPlan.Final(FirstRunOrNull(current.Add(InitialDelay.Value)));
            }
        }

        // Calculate next occurrence from the appropriate base time:
        // - If SpecificRunTime is set and in the past, calculate from SpecificRunTime to properly skip past occurrences
        // - Otherwise, calculate from current time
        var baseTime = (currentRun == 0 && runtime.HasValue && runtime.Value < current)
            ? runtime.Value
            : current;

        return new NextRunPlan(false, null, baseTime, runtime, current, currentRun);
    }

    /// <summary>
    /// The other half: what the schedule's next run is, given the grid's answer for
    /// <see cref="NextRunPlan.BaseTime"/>.
    /// </summary>
    /// <param name="plan">What <see cref="PlanNextRun"/> decided.</param>
    /// <param name="next">The grid's occurrence strictly after the plan's base time.</param>
    /// <param name="gridCollapsed">How many nominal slots a DST transition folded into it.</param>
    /// <param name="collapsedSlots">
    /// The collapse count of the occurrence really returned — zero whenever a first-run instant wins over the
    /// grid's slot, because a count belonging to a slot nobody schedules would be a lie.
    /// </param>
    internal DateTimeOffset? SelectNextRun(in NextRunPlan plan, DateTimeOffset? next, int gridCollapsed,
                                           out int collapsedSlots)
    {
        collapsedSlots = 0;

        if (plan.CurrentRun > 0)
        {
            collapsedSlots = gridCollapsed;
            return next;
        }

        if (next == null) return FirstRunOrNull(plan.Runtime);

        // For RunNow or SpecificRunTime, use runtime if:
        // 1. It's in the future (always use future SpecificRunTime)
        // 2. It's in the recent past (within 20 seconds) AND before next interval
        // No arbitrary gap required - the user explicitly requested this runtime
        if (plan.Runtime is { } runtime)
        {
            // If runtime is in the future, always use it
            if (runtime > plan.Current)
            {
                return FirstRunOrNull(runtime);
            }

            // If runtime is in the recent past, use it only if it's before next interval
            var runtimeIsBeforeNext = runtime < next;
            var notTooFarInPast = runtime > plan.Current.AddSeconds(-20);

            if (runtimeIsBeforeNext && notTooFarInPast)
            {
                return FirstRunOrNull(runtime);
            }
        }

        collapsedSlots = gridCollapsed;
        return next;
    }

    /// <summary>
    /// The first occurrence (RunNow / SpecificRunTime / InitialDelay) must be validated against
    /// <see cref="RunUntil"/> too: only subsequent occurrences are, so a first run beyond it would fire
    /// anyway.
    /// </summary>
    private DateTimeOffset? FirstRunOrNull(DateTimeOffset? candidate) =>
        candidate.HasValue && RunUntil.HasValue && candidate.Value >= RunUntil.Value ? null : candidate;

    /// <summary>
    /// Calculates the minimum interval for this recurring task.
    /// For cron expressions, calculates the interval between the next two occurrences.
    /// For interval-based tasks, returns the configured interval.
    /// </summary>
    /// <returns>Minimum interval between executions</returns>
    /// <remarks>
    /// Kept as its own zero-argument method rather than an optional parameter on the overload below: the
    /// original IL signature is what an assembly compiled against the previous release calls.
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

    internal const int MaxExclusionSearchIterations = 200_000;

    private DateTimeOffset? GetNextOccurrence(DateTimeOffset current) => GetNextOccurrence(current, out _);

    /// <param name="collapsedSlots">
    /// How many nominal wall-clock slots a daylight-saving transition folded into the returned occurrence.
    /// Always 0 for a schedule the zone does not govern and for cron, whose transition rules are Cronos's own.
    /// </param>
    private DateTimeOffset? GetNextOccurrence(DateTimeOffset current, out int collapsedSlots)
    {
        if (Exclusions == null)
            return GetNextOccurrenceUnfiltered(current, out collapsedSlots);

        var baseGrid = Base();
        var candidate = GetNextBaseOccurrence(baseGrid, current, out var candidateCollapsed);

        return FilterExcludedCandidates(baseGrid, candidate, candidateCollapsed, out collapsedSlots);
    }

    private DateTimeOffset? GetNextOccurrenceUnfiltered(DateTimeOffset current, out int collapsedSlots)
    {
        collapsedSlots = 0;

        // The single door into the arithmetic grid, which is why the refusal lives here: with no interval and
        // no cron the cascade below answers "no occurrence, ever" for a provider-driven schedule, and every
        // primitive on this class would then quietly report a series that has ended. A provider grid is
        // asynchronous and is reached through IScheduleEvaluator; nothing inside the library asks this one.
        if (Provider is { } provider)
        {
            throw new NotSupportedException(
                $"This schedule takes its occurrences from the provider '{provider.Key}', which answers " +
                "asynchronously: ask the schedule evaluator instead of the definition's own arithmetic.");
        }

        var zone = GoverningZone;

        if (!string.IsNullOrEmpty(CronInterval?.CronExpression))
        {
            // Cronos owns the DST rules for a cron expression and is the oracle the fluent API is measured
            // against, so the zone is handed to it rather than reimplemented around it.
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

    private DateTimeOffset? FilterExcludedCandidates(RecurringTask baseGrid, DateTimeOffset? candidate,
                                                      int candidateCollapsed, out int collapsedSlots)
    {
        collapsedSlots = 0;

        // Every advance starts from a real unfiltered occurrence. A discarded occurrence's DST collapse
        // count is replaced with the next candidate's count instead of leaking onto the returned slot.
        for (var discarded = 0; candidate is { } current; discarded++)
        {
            if (RunUntil <= current)
                return null;

            if (!TryGetExclusionRegionExit(current, out var exit))
            {
                collapsedSlots = candidateCollapsed;
                return current;
            }

            if (exit == null)
                return null;

            if (discarded + 1 > MaxExclusionSearchIterations)
                throw new ExclusionSearchBudgetExceededException(current);

            var next = AdvanceBasePastExcludedRegion(baseGrid, current, exit.Value, out candidateCollapsed);
            if (next is { } value && value <= current)
                return null;

            candidate = next;
        }

        return null;
    }

    private DateTimeOffset? AdvanceBasePastExcludedRegion(RecurringTask baseGrid, DateTimeOffset candidate,
                                                           DateTimeOffset exit, out int collapsedSlots)
    {
        collapsedSlots = 0;

        if (baseGrid.IsUniformGrid())
        {
            var jumped = TryJumpBaseUniformGrid(baseGrid, candidate, exit.AddTicks(-1));
            if (jumped.HasValue)
                return jumped;
        }

        if (!string.IsNullOrEmpty(baseGrid.CronInterval?.CronExpression))
        {
            var next = baseGrid.CronInterval.GetNextOccurrence(
                exit.AddTicks(-1), baseGrid.GoverningZone ?? TimeZoneInfo.Utc);

            return next == null || baseGrid.RunUntil <= next ? null : next;
        }

        return GetNextBaseOccurrence(baseGrid, candidate, out collapsedSlots);
    }

    private static DateTimeOffset? GetNextBaseOccurrence(RecurringTask baseGrid, DateTimeOffset current,
                                                          out int collapsedSlots)
    {
        // An exclusion search may legitimately reach the end of DateTimeOffset; the legacy no-exclusion
        // path remains untouched and keeps its historical overflow behaviour.
        try
        {
            return baseGrid.GetNextOccurrence(current, out collapsedSlots);
        }
        catch (ArgumentOutOfRangeException)
        {
            collapsedSlots = 0;
            return null;
        }
    }

    private static DateTimeOffset? TryJumpBaseUniformGrid(RecurringTask baseGrid, DateTimeOffset anchor,
                                                          DateTimeOffset after)
    {
        try
        {
            return baseGrid.TryJumpUniformGrid(anchor, after);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private bool TryGetExclusionRegionExit(DateTimeOffset candidate, out DateTimeOffset? exit)
    {
        exit = null;
        var exclusions = Exclusions!;

        if (exclusions.Days.Length > 0 || exclusions.Dates.Length > 0)
        {
            var exclusionZone = Zone ?? TimeZoneInfo.Utc;
            var localDate = DateOnly.FromDateTime(WallClock.ToWall(candidate, exclusionZone).DateTime);

            if (exclusions.Days.Contains(localDate.DayOfWeek) || exclusions.Dates.Contains(localDate))
            {
                if (localDate == DateOnly.MaxValue)
                    return true;

                var nextMidnight = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
                exit = WallClock.ToUtc(nextMidnight, exclusionZone, candidate).Utc;
            }
        }

        foreach (var range in exclusions.Ranges)
        {
            if (candidate < range.FromUtc || candidate >= range.ToUtc)
                continue;

            if (exit == null || range.ToUtc > exit)
                exit = range.ToUtc;
        }

        return exit != null;
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
    /// components, and the nominal slot it lands on is mapped back to an instant.
    /// </summary>
    /// <remarks>
    /// The loop exists for the one case a single mapping cannot answer: a slot whose instant is at or before
    /// the one we started from — a nominal time a DST gap collapsed onto an instant already served, or the
    /// second reading of a repeated hour. It advances from the NOMINAL wall slot, never from the shifted
    /// instant, so several nominal slots inside one gap produce exactly one occurrence and the repeated hour
    /// fires once, on its first pass. Slots producing no occurrence of their own are counted into
    /// <see cref="WallMapping.CollapsedCount"/> from two places: the loop's own discards, and — for a slot a
    /// gap moved — the later slots up to the gap's exit, which the walk resumes AT and never sees.
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

    // The walk's bound for the ask that carries NO cap — the logging-only skip count, where under-reporting
    // costs nothing but a smaller number in a log line. It is NOT a second bound on a caller that passed one:
    // a walk stopping here would answer "10,001" to a question asked with a larger cap, read as a total.
    private const int MaxSkipCountIterations = 10_000;

    /// <summary>
    /// First real occurrence strictly after <paramref name="after"/>, anchored on the known real
    /// occurrence <paramref name="anchor"/> (with <c>anchor &lt;= after</c>). This is the single
    /// skip-forward primitive shared by every schedule kind — the calendar-aware generalisation of the
    /// cron realignment. Returns <c>null</c> if the series ends (<see cref="RunUntil"/>) before any such
    /// occurrence. The normal first-run path (<see cref="CalculateNextRun(DateTimeOffset,int,bool)"/> /
    /// <see cref="GetNextOccurrence(DateTimeOffset)"/>)
    /// is the single source of truth for the occurrence grid; this method never re-derives it.
    /// </summary>
    internal DateTimeOffset? NextOccurrenceStrictlyAfter(DateTimeOffset anchor, DateTimeOffset after)
    {
        // Series already ended at `after`: any occurrence strictly after it is past RunUntil. O(1) for every
        // path, and it stops a uniform series from walking millions of steps just to discover the end.
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
        if (Exclusions == null && IsUniformGrid())
        {
            var jumped = TryJumpUniformGrid(anchor, after);
            if (jumped.HasValue)
                return RunUntil.HasValue && jumped.Value >= RunUntil.Value ? null : jumped;
        }

        if (Exclusions != null)
        {
            var baseGrid = Base();
            if (baseGrid.IsUniformGrid())
            {
                var jumped = TryJumpBaseUniformGrid(baseGrid, anchor, after);
                if (jumped.HasValue)
                {
                    if (RunUntil.HasValue && jumped.Value >= RunUntil.Value)
                        return null;

                    return FilterExcludedCandidates(baseGrid, jumped, candidateCollapsed: 0, out _);
                }
            }
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

        // Cap hit (pathological — a real coarse schedule never reaches it). NEVER return a value <= after: a
        // stale past next-run is scheduled immediately, re-fires and consumes a run.
        var tail = GetNextOccurrence(occurrence);
        return tail.HasValue && tail.Value > after ? tail : null;
    }

    /// <summary>
    /// How many steps the backfill walk may take before giving up. The probe below starts at most a couple of
    /// periods before the answer, so two or three steps is the real cost; the bound is here so a pathological
    /// definition cannot spin.
    /// </summary>
    private const int MaxBackfillProbeSteps = 64;

    /// <summary>
    /// How far back the backfill probe may reach, as a multiple of the schedule's own period, before it gives
    /// up looking for a slot at or before <c>instant</c>. Doubling from one period, so a period the estimate
    /// underestimates (a 30-day month, a day interval riding on <c>OnDays</c>) is still covered.
    /// </summary>
    private const int MaxBackfillProbeBackoffs = 8;

    /// <summary>
    /// The first occurrence at or ON <paramref name="instant"/> — the cursor a backfilled durable schedule
    /// starts from. Null when the grid produces none, or when the series already ends before it.
    /// </summary>
    /// <remarks>
    /// Every other question the grid answers is "strictly after", and a probe placed just before
    /// <paramref name="instant"/> is wrong for most of them: a Day, Week or Month interval advances its
    /// PERIOD before choosing a time inside it, so its first answer already sits a whole period past the slot
    /// asked for, and a forward-only walk never comes back for it. The probe therefore steps BACK by a period
    /// first, doubling while the grid still answers past <paramref name="instant"/>, since the period
    /// estimate is approximate for months and for day-of-week selectors.
    /// </remarks>
    internal DateTimeOffset? FirstOccurrenceOnOrAfter(DateTimeOffset instant)
    {
        var slot = FirstOccurrenceAfterBackfillProbe(instant);

        for (var i = 0; i < MaxBackfillProbeSteps && slot is { } current && current < instant; i++)
        {
            var next = GetNextOccurrence(current);
            if (next is null || next <= current)
                return null;

            slot = next;
        }

        if (slot is { } value && value < instant && Exclusions != null)
            throw new ExclusionSearchBudgetExceededException(value);

        return slot is { } candidate && candidate >= instant ? candidate : null;
    }

    /// <summary>
    /// The grid's first answer from a probe far enough before <paramref name="instant"/> that the walk cannot
    /// have already passed the slot being looked for.
    /// </summary>
    private DateTimeOffset? FirstOccurrenceAfterBackfillProbe(DateTimeOffset instant)
    {
        // Anchored on `instant`, never on the wall clock: this runs on the dispatch path, where every
        // scheduling decision reads the injected clock, and a cron's period is measured by probing it.
        var period = GetMinimumInterval(instant);

        if (period <= TimeSpan.Zero)
            period = TimeSpan.FromSeconds(1);

        // A MONTH is the one period a span cannot express: the estimate's flat 30 days lands on a different
        // day of the month whenever the month is not 30 days long, and a month grid keeps the day its anchor
        // had (AddMonths first, then the day selectors walk FORWARD from it), so a probe one or two days off
        // carries a different PHASE the forward-only walk can never come back for. Every other period is a
        // constant step, where subtracting the span and stepping the calendar are the same instant.
        int? months = ProbeStepsByMonths ? 1 : null;

        DateTimeOffset? first = null;

        for (var i = 0; i < MaxBackfillProbeBackoffs; i++)
        {
            // Only representable instants: a schedule anchored near DateTimeOffset.MinValue cannot step back
            // any further, and the last answer it gave is the best one there is.
            if (instant - DateTimeOffset.MinValue <= period)
                return first ?? GetNextOccurrence(DateTimeOffset.MinValue);

            first = GetNextOccurrence(ProbeFrom(instant, months, period));

            // The probe reached a slot at or before `instant`: the walk in the caller now only has to come
            // forward, and it cannot skip anything on the way.
            if (first is not { } candidate || candidate <= instant)
                return first;

            period += period;
            months *= 2;
        }

        return first;
    }

    /// <summary>
    /// Where the probe starts: <paramref name="months"/> calendar months before <paramref name="instant"/> for
    /// a month cadence, <paramref name="period"/> before it for every other shape.
    /// </summary>
    /// <remarks>
    /// The calendar step falls back to the span at the very bottom of the representable range, where
    /// <c>AddMonths</c> would throw: 31 days is the longest a calendar month can be, so the comparison is the
    /// exact condition under which it cannot.
    /// </remarks>
    private static DateTimeOffset ProbeFrom(DateTimeOffset instant, int? months, TimeSpan period) =>
        months is { } back && instant - DateTimeOffset.MinValue > TimeSpan.FromDays(31.0 * back)
            ? instant.AddMonths(-back)
            : instant - period;

    /// <summary>
    /// True when this schedule's own period is a calendar MONTH — the one period a flat span cannot express.
    /// </summary>
    /// <remarks>
    /// Mirrors the branch <see cref="GetMinimumInterval(DateTimeOffset?)"/> answers its 30-day approximation
    /// from: cron and every finer interval come first there, and all of them are constant steps.
    /// </remarks>
    private bool ProbeStepsByMonths =>
        string.IsNullOrEmpty(CronInterval?.CronExpression)
        && SecondInterval?.Interval is not > 0
        && MinuteInterval?.Interval is not > 0
        && HourInterval?.Interval is not > 0
        && DayInterval?.Interval is not > 0
        && WeekInterval?.Interval is not > 0
        && MonthInterval?.Interval is > 0;

    /// <summary>
    /// The NATURAL successor of <paramref name="occurrence"/> on the occurrence grid, computed while
    /// IGNORING the termination bounds (<see cref="RunUntil"/> / <see cref="MaxRuns"/>). Returns
    /// <c>null</c> only when the grid itself cannot produce one.
    /// </summary>
    /// <remarks>
    /// This is what the recovery grace window must ask. The bounded successor is null once the series ends,
    /// so a slot months old could otherwise be executed at restart. Asking the unbounded grid separates the
    /// two: a successor still in the future means the stored slot is genuinely the current one (a grace
    /// window as wide as one period, be it a minute or a month), a successor already past means the slot is
    /// stale and the series must be finalized instead.
    /// </remarks>
    internal DateTimeOffset? NextGridOccurrenceAfter(DateTimeOffset occurrence) =>
        Unbounded().NextOccurrenceStrictlyAfter(occurrence, occurrence);

    /// <summary>
    /// <see cref="FirstOccurrenceOnOrAfter"/> on the natural grid, with the termination bounds IGNORED.
    /// </summary>
    /// <remarks>
    /// What a rebase counts with: how far into its period a cursor stood is a question about the GRID, and a
    /// <see cref="RunUntil"/> falling inside the period would truncate the count and move the answer to an
    /// earlier slot — one that has already run.
    /// </remarks>
    internal DateTimeOffset? FirstGridOccurrenceOnOrAfter(DateTimeOffset instant) =>
        Unbounded().FirstOccurrenceOnOrAfter(instant);

    /// <summary>
    /// A copy of this definition with <see cref="RunUntil"/> and <see cref="MaxRuns"/> cleared.
    /// </summary>
    /// <remarks>
    /// Shallow: the interval objects are only read while walking the grid, so clearing the bounds on the copy
    /// is what makes a walk unbounded without mutating the live definition.
    /// </remarks>
    private RecurringTask Unbounded()
    {
        var unbounded = (RecurringTask)MemberwiseClone();
        unbounded.RunUntil = null;
        unbounded.MaxRuns  = null;

        return unbounded;
    }

    /// <summary>A shallow copy exposing the inclusion grid without its fixed exclusions.</summary>
    private RecurringTask Base()
    {
        var baseGrid = (RecurringTask)MemberwiseClone();
        baseGrid.Exclusions = null;

        return baseGrid;
    }

    /// <summary>
    /// True when <see cref="CountMissedOccurrences"/> answers by division instead of walking the grid.
    /// </summary>
    /// <remarks>
    /// The cap exists to keep a WALK affordable; on a grid that counts in one subtraction it buys nothing and
    /// only turns a real total into a lower bound. A caller that has to report the number — how many runs a
    /// downtime cost — asks this before deciding whether to cap at all.
    /// </remarks>
    internal bool CountsMissedInConstantTime() =>
        string.IsNullOrEmpty(CronInterval?.CronExpression) && IsUniformGrid();

    /// <summary>
    /// Whether <paramref name="count"/> — a <see cref="CountMissedOccurrences"/> answer taken WITHOUT a cap —
    /// is the real total or only a lower bound.
    /// </summary>
    /// <remarks>
    /// The uncapped ask falls back to the walk's own bound, so its answer can stop short with nothing on the
    /// number to say so. The rule lives here because that bound is private and the callers that REPORT the
    /// count must not present a truncated walk as a total.
    /// </remarks>
    internal bool IsExactUncappedCount(int count) =>
        CountsMissedInConstantTime() || count <= MaxSkipCountIterations;

    /// <summary>
    /// Number of occurrences missed in <c>(anchor, after]</c>, reported for LOGGING ONLY: it never consumes
    /// the <see cref="MaxRuns"/> budget. Uniform grids count in O(1) by division; calendar/cron schedules
    /// walk the real schedule, bounded.
    /// </summary>
    /// <param name="cap">
    /// Upper bound on the returned count: the walk stops at <c>cap + 1</c> and the arithmetic result is
    /// clamped there. Whatever cap is passed is HONOURED — a walked grid takes as many steps as the cap asks
    /// for — which is what makes <c>result &lt;= cap</c> the test that tells a real total from a lower bound.
    /// The default, <c>int.MaxValue</c>, is "no cap": the walk then keeps
    /// <see cref="MaxSkipCountIterations"/> as its own bound and the answer may be a lower bound with nothing
    /// to say so, which is why it is only asked for where the number goes to a log line.
    /// </param>
    internal int CountMissedOccurrences(DateTimeOffset anchor, DateTimeOffset after, int cap = int.MaxValue)
    {
        // cap + 1 in long arithmetic: cap == int.MaxValue must not wrap to a negative ceiling.
        var ceiling = Math.Min((long)cap + 1, int.MaxValue);

        // Uniform grid: O(1) division (mirrors the historical simple-interval skip count, and keeps a
        // 1-second interval over a year-long downtime O(1) instead of tens of millions of walk steps).
        if (CountsMissedInConstantTime())
        {
            var nextUniform = GetNextOccurrence(anchor);
            if (nextUniform == null)
                return 1; // the anchor itself is the (only) missed occurrence

            var stepTicks = (nextUniform.Value - anchor).Ticks;
            if (stepTicks <= 0)
                return 1;

            // Clamp the horizon to RunUntil, which is EXCLUSIVE everywhere else on the grid: the clamped span
            // stops one tick short of it, or this path answers one more than the walk and a catch-up cap of
            // ten reads eleven due slots where there are ten. The span runs from the anchor INCLUSIVE, to
            // match the walk's convention below.
            var spanTicks = (after - anchor).Ticks;

            if (RunUntil is { } end && end <= after)
                spanTicks = (end - anchor).Ticks - 1;

            if (spanTicks < 0)
                spanTicks = 0;

            var count = spanTicks / stepTicks + 1;
            return (int)Math.Min(count, ceiling);
        }

        // Calendar / cron: walk the real schedule (the anchor itself is the first missed occurrence). The
        // caller's cap IS the bound and the walk is O(cap) on purpose, because a caller deciding on this
        // number must be able to tell "cap + 1, so there are more" from "this many, and that is all". Only
        // int.MaxValue, the absence of a cap, falls back to the walk's own bound.
        var maxSteps = cap == int.MaxValue ? MaxSkipCountIterations : ceiling - 1;

        var skipped    = 1;
        var occurrence = anchor;
        for (long i = 0; i < maxSteps; i++)
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
    /// the field's <c>Interval</c> value, because <c>OnDays</c> rides on <c>DayInterval(Interval=0)</c>.
    /// Conservative: when in doubt the answer is "not uniform", which is always correct (slower).
    /// </summary>
    internal bool IsUniformGrid()
    {
        if (Exclusions != null) return false;

        // A calendar schedule read on a real clock has no constant step — local midnight is 24 hours after
        // the previous one on every day but the two the zone changes offset on. Elapsed grids and plain-UTC
        // calendars keep the arithmetic they always had.
        if (GoverningZone != null) return false;

        // A provider names no cadence at all, so it can never be a fixed progression — and the O(1) jump would
        // measure a "step" by asking it twice.
        if (Provider != null) return false;

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
        // irregular and would be mistaken for the cadence, landing off-grid. `first` is always a real on-grid
        // occurrence, so jumping from it stays grid-aligned.
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

        // Never let the tick arithmetic run past DateTimeOffset.MaxValue — return null (no representable
        // future occurrence) instead of throwing ArgumentOutOfRangeException.
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
        // needless walk: the caller gates RunUntil once on the returned candidate.
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
            parts.Add($"Run at {OnScheduleClock(SpecificRunTime.Value)}");

        if (parts.Count > 0)
            parts.Add("then");

        if (Provider != null)
        {
            parts.Add(Provider.Describe());
            AppendExclusions(parts);
            AppendBounds(parts);
            AppendModifiers(parts);
            return string.Join(" ", parts);
        }

        if (CronInterval != null)
        {
            parts.Add("Use Cron expression:");
            parts.Add(CronInterval.CronExpression);
            AppendExclusions(parts);
            AppendBounds(parts);
            AppendModifiers(parts);
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

        AppendExclusions(parts);
        AppendBounds(parts);
        AppendModifiers(parts);

        return string.Join(" ", parts);
    }

    private void AppendExclusions(List<string> parts)
    {
        if (Exclusions is not { } exclusions)
            return;

        var details = new List<string>();
        var days = exclusions.Days ?? [];
        var dates = exclusions.Dates ?? [];
        var ranges = exclusions.Ranges ?? [];

        if (days.Length > 0)
        {
            details.Add(string.Join(" - ", days.OrderBy(static day => day == DayOfWeek.Sunday ? 7 : (int)day)));
        }

        if (dates.Length is > 0 and <= 3)
            details.Add(string.Join(" - ", dates.Select(static date => $"{date:yyyy-MM-dd}")));
        else if (dates.Length > 3)
            details.Add($"{dates.Length} dates");

        if (ranges.Length is > 0 and <= 3)
        {
            details.Add(string.Join(" - ", ranges
                .Select(range => $"[{OnScheduleClock(range.FromUtc)}, {OnScheduleClock(range.ToUtc)})")));
        }
        else if (ranges.Length > 3)
        {
            details.Add($"{ranges.Length} windows");
        }

        if (details.Count > 0)
            parts.Add($"except {string.Join(", ", details)}");
    }

    /// <summary>The termination bounds, which read the same whatever produced the occurrences.</summary>
    private void AppendBounds(List<string> parts)
    {
        if (RunUntil != null)
            parts.Add($"until {OnScheduleClock(RunUntil.Value)}");

        if (MaxRuns != null)
            parts.Add($"up to {MaxRuns} times");
    }

    /// <summary>
    /// An absolute instant read on the clock this description is written on: the schedule's own zone — the id
    /// <see cref="AppendModifiers"/> prints right after it — and UTC, named, for a schedule that has none.
    /// </summary>
    /// <remarks>
    /// Never the HOST's zone: this string is persisted as <c>QueuedTask.RecurringInfo</c> and shown by the
    /// dashboard, so the same definition dispatched from a UTC container and from a developer machine would
    /// write two different sentences for one bound. Falls back to UTC rather than throwing when the stored id
    /// no longer resolves, like <see cref="ToScheduleLocalTime"/>.
    /// </remarks>
    private string OnScheduleClock(DateTimeOffset instant) =>
        ToScheduleLocalTime(instant) is { } local
            ? $"{local:yyyy-MM-dd HH:mm:ss}"
            : $"{instant.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";

    /// <summary>
    /// The zone is part of what the schedule MEANS, and so is how it treats a missed slot, so the
    /// human-readable form names both. Absent for a schedule that declares neither, which keeps every
    /// description written before they existed unchanged.
    /// </summary>
    private void AppendModifiers(List<string> parts)
    {
        if (TimeZoneId != null)
            parts.Add($"({TimeZoneId})");

        if (!IsDurable)
            return;

        parts.Add("[durable occurrences,");
        parts.Add($"{(Misfire?.Describe() ?? "on misfire skip")}]");
    }

    #endregion
}

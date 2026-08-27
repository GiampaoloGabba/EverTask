namespace EverTask.Scheduler.Recurring.Builder;

/// <summary>
/// Collects the ONE misfire policy an <c>OnMisfire</c> callback selects, and refuses zero or two.
/// </summary>
/// <remarks>
/// A callback that selects nothing is a schedule the caller believes has a policy and does not; a callback
/// that selects twice is two intentions, only one of which would survive. Both are mistakes worth a throw at
/// build time rather than a surprise months into a backlog.
/// </remarks>
internal sealed class MisfirePolicyBuilder : IMisfirePolicyBuilder
{
    private MisfireSettings? _settings;

    public void Skip() => Select(new MisfireSettings { Policy = MisfirePolicy.Skip });

    public void FireOnce(FireOnceOptions? options = null) =>
        Select(new MisfireSettings { Policy = MisfirePolicy.FireOnce, MaxAge = options?.MaxAge });

    public void CatchUp(CatchUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Select(new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = options.MaxAge,
            MaxOccurrences        = options.MaxOccurrences,
            OverflowPolicy        = options.OverflowPolicy,
            MaxPendingOccurrences = options.MaxPendingOccurrences
        });
    }

    private void Select(MisfireSettings settings)
    {
        if (_settings != null)
        {
            throw new InvalidOperationException(
                $"OnMisfire already selected '{_settings.Policy}': a schedule has exactly one misfire policy.");
        }

        _settings = settings;
    }

    internal MisfireSettings Build() =>
        _settings ?? throw new InvalidOperationException(
            "OnMisfire selected no policy. Call Skip(), FireOnce(...) or CatchUp(...) inside the callback.");
}

/// <summary>
/// The three schedule-wide modifiers every builder in this namespace exposes, applied to the definition being
/// built. One implementation instead of seven, so the rules that tie them together cannot drift per builder.
/// </summary>
internal static class ScheduleModifiers
{
    /// <summary>
    /// Applies the selected policy and, when it replays missed work, turns the schedule durable: a replay
    /// needs one durable row per slot, so the two are one decision.
    /// </summary>
    internal static void OnMisfire(RecurringTask task, Action<IMisfirePolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new MisfirePolicyBuilder();
        configure(builder);

        var settings = builder.Build();
        settings.Validate();

        task.Misfire = settings;

        if (settings.Policy != MisfirePolicy.Skip)
            task.OccurrenceMode = OccurrenceMode.Durable;
    }

    internal static void WithDurableOccurrences(RecurringTask task) =>
        task.OccurrenceMode = OccurrenceMode.Durable;

    /// <summary>
    /// Sets the backfill start and turns the schedule durable, for the same reason as a replaying policy:
    /// there is nowhere to put a backfilled occurrence on an inline schedule.
    /// </summary>
    internal static void BackfillFrom(RecurringTask task, DateTimeOffset startUtc)
    {
        task.BackfillFromUtc = startUtc.ToUniversalTime();
        task.OccurrenceMode  = OccurrenceMode.Durable;
    }
}

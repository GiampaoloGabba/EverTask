namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// The occurrence grid of a schedule whose slots come from an <see cref="INextOccurrenceProvider"/>: the same
/// questions <see cref="RecurringTask"/> answers by arithmetic, answered by asking the provider.
/// </summary>
/// <remarks>
/// Every question reduces to the ONE thing a provider can answer — "which occurrence follows this instant" —
/// so a walk here is a series of round trips and every walk is bounded. The non-grid parts of the schedule
/// math (first-run configuration, termination bounds, realignment past a downtime) are REUSED from the
/// built-in path rather than mirrored, with a provider call where it would have stepped an interval.
/// A provider that throws, or answers out of contract, is TRANSIENT: the failure is wrapped in an
/// <see cref="OccurrenceProviderException"/> carrying the backoff its schedule has earned, nothing here
/// writes anything, and the caller decides what to do with a schedule that has no answer yet.
/// </remarks>
internal sealed class ProviderScheduleGrid(
    OccurrenceProviderRegistry registry,
    OccurrenceProviderRetryRegistry retries,
    IEverTaskLogger<ProviderScheduleGrid> logger)
{
    /// <summary>
    /// How far any diagnostic count over a provider grid may walk. Each step is a round trip, so a number
    /// produced past this bound travels as a lower bound and says so.
    /// </summary>
    internal const int MaxDiagnosticWalk = 250;

    /// <summary>
    /// The cap a DIAGNOSTIC count may spend on <paramref name="definition"/>'s grid: <paramref name="walkedCap"/>
    /// when the walk happens in memory, <see cref="MaxDiagnosticWalk"/> when every step of it is a round trip.
    /// </summary>
    /// <remarks>
    /// For diagnostic counts only — those reaching a log line, an event or a result field. A count that feeds
    /// a DECISION keeps the bound its caller passed whatever the grid is, since a second smaller bound would
    /// make "more than the cap" indistinguishable from "exactly the cap".
    /// </remarks>
    internal static int DiagnosticCapFor(RecurringTask definition, int walkedCap) =>
        definition.Provider is null ? walkedCap : MaxDiagnosticWalk;

    /// <summary>
    /// How many slots a count over a provider grid may walk for a given cap: <paramref name="cap"/><c> + 1</c>,
    /// which is what tells "more than the cap" from "exactly the cap", or
    /// <see cref="MaxDiagnosticWalk"/><c> + 1</c> for the ask that carries no cap at all.
    /// </summary>
    /// <remarks>
    /// Shared with the <c>SkipOldest</c> search in <see cref="DueSlotEnumerator"/>, which compares its own
    /// memoized probes against the counts this class gives: two copies of the rule would diverge.
    /// </remarks>
    internal static long CountCeiling(int cap) =>
        cap == int.MaxValue ? MaxDiagnosticWalk + 1 : Math.Min((long)cap + 1, int.MaxValue);

    /// <summary>
    /// <see cref="RecurringTaskExtensions.CalculateNextValidRun(RecurringTask,DateTimeOffset,int,DateTimeOffset?,bool,bool,DateTimeOffset?)"/>
    /// over a provider grid: the same decision, with the interval step replaced by a provider call.
    /// </summary>
    public async ValueTask<NextRunResult> CalculateNextValidRunAsync(
        RecurringTask definition, DateTimeOffset scheduledTime, int currentRun, DateTimeOffset nowUtc,
        DateTimeOffset? referenceTime, bool isRecovery, bool computeSkippedCount, ScheduleIdentity identity,
        CancellationToken ct)
    {
        var plan = definition.PlanNextRun(scheduledTime, currentRun, isRecovery, nowUtc);

        var nextRun = plan.IsFinal
                          ? plan.Answer
                          : definition.SelectNextRun(plan,
                              await BoundedAfterAsync(definition, plan.BaseTime, identity, ct).ConfigureAwait(false),
                              0, out _);

        var now = referenceTime ?? nowUtc;

        if (!nextRun.HasValue || nextRun.Value >= now.AddSeconds(-RecurringTaskExtensions.ToleranceSeconds))
            return new NextRunResult(nextRun, 0);

        var next = await NextAfterAsync(definition, nextRun.Value, now, identity, ct).ConfigureAwait(false);

        // The anchor rule is the built-in one: on recovery the stored slot was itself missed and counts, after
        // a run the occurrence that just executed did not.
        var countAnchor = isRecovery ? scheduledTime : nextRun.Value;

        // Bounded far shorter than a walked calendar's, because every step here is a round trip. What it costs
        // is exactness past the bound, and the number says so instead of arriving as a total.
        var skipped = computeSkippedCount
                          ? await CountMissedAsync(definition, countAnchor, now, MaxDiagnosticWalk, identity, ct)
                                .ConfigureAwait(false)
                          : 0;

        return new NextRunResult(next, skipped) { SkippedCountIsExact = skipped <= MaxDiagnosticWalk };
    }

    /// <summary>
    /// <see cref="RecurringTask.NextOccurrenceStrictlyAfter"/> over a provider grid. The anchor plays no part:
    /// a provider answers about an instant, never by walking from a known occurrence.
    /// </summary>
    public ValueTask<DateTimeOffset?> NextAfterAsync(RecurringTask definition, DateTimeOffset anchor,
                                                     DateTimeOffset after, ScheduleIdentity identity,
                                                     CancellationToken ct) =>
        definition.RunUntil is { } end && after >= end
            ? new ValueTask<DateTimeOffset?>((DateTimeOffset?)null)
            : BoundedAfterAsync(definition, after, identity, ct);

    /// <summary>
    /// <see cref="RecurringTask.NextGridOccurrenceAfter"/> over a provider grid: the natural successor, with
    /// the termination bounds ignored (the recovery grace window).
    /// </summary>
    public ValueTask<DateTimeOffset?> NextGridOccurrenceAfterAsync(RecurringTask definition,
                                                                   DateTimeOffset occurrence,
                                                                   ScheduleIdentity identity,
                                                                   CancellationToken ct) =>
        AskAsync(definition, occurrence, identity, ct);

    /// <summary>
    /// <see cref="RecurringTask.FirstOccurrenceOnOrAfter"/> over a provider grid.
    /// </summary>
    /// <remarks>
    /// One call, against the tick before the instant: a provider answers about an absolute instant, so
    /// "strictly after one tick earlier" IS "on or after". The built-in grid cannot do that — its day, week
    /// and month intervals advance their period before choosing a time inside it.
    /// </remarks>
    public ValueTask<DateTimeOffset?> FirstOccurrenceOnOrAfterAsync(RecurringTask definition, DateTimeOffset instant,
                                                                    ScheduleIdentity identity, CancellationToken ct) =>
        BoundedAfterAsync(definition, instant > DateTimeOffset.MinValue ? instant.AddTicks(-1) : instant, identity,
            ct);

    /// <summary>
    /// <see cref="RecurringTask.CountMissedOccurrences"/> over a provider grid: the anchor plus every slot up
    /// to <paramref name="after"/>, one call each, stopping at <paramref name="cap"/><c> + 1</c>.
    /// </summary>
    public async ValueTask<int> CountMissedAsync(RecurringTask definition, DateTimeOffset anchor,
                                                 DateTimeOffset after, int cap, ScheduleIdentity identity,
                                                 CancellationToken ct)
    {
        // A cap of int.MaxValue means "no cap", which no provider grid can afford: the walk keeps its own
        // bound instead.
        var ceiling = CountCeiling(cap);

        var walked = await BoundedOccurrenceWalker
                           .WalkAsync(anchor, after, (int)ceiling, includeStart: true,
                               slot => NextAfterAsync(definition, slot, slot, identity, ct))
                           .ConfigureAwait(false);

        return walked.Count;
    }

    /// <summary>The provider's answer, with <c>RunUntil</c> applied to it.</summary>
    private async ValueTask<DateTimeOffset?> BoundedAfterAsync(RecurringTask definition, DateTimeOffset after,
                                                               ScheduleIdentity identity, CancellationToken ct)
    {
        var slot = await AskAsync(definition, after, identity, ct).ConfigureAwait(false);

        return slot is { } value && definition.RunUntil is { } end && value >= end ? null : slot;
    }

    /// <summary>
    /// The one call: builds the request, resolves the provider in its own scope and classifies whatever comes
    /// back.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No provider is registered under the schedule's key. A configuration error, never a transient one: it
    /// reaches a dispatch as a refusal and a persisted row as the poison route every corrupt schedule takes.
    /// </exception>
    /// <exception cref="OccurrenceProviderException">
    /// The provider threw, or answered at or before the instant it was asked about.
    /// </exception>
    private async ValueTask<DateTimeOffset?> AskAsync(RecurringTask definition, DateTimeOffset after,
                                                      ScheduleIdentity identity, CancellationToken ct)
    {
        var settings = definition.Provider
                       ?? throw new InvalidOperationException(
                           "This schedule has no occurrence provider; its grid is the built-in one.");

        // Asked BEFORE the call and outside the try: an unregistered key and a provider that threw an
        // ArgumentException of its own are opposite verdicts — one is configuration, the other is transient —
        // and catching around the lookup would make them indistinguishable.
        if (!registry.IsRegistered(settings.Key))
            throw registry.UnknownKey(settings.Key);

        var request = new NextOccurrenceRequest
        {
            ProviderKey = settings.Key,
            Config      = settings.Config,
            AfterUtc    = after.ToUniversalTime(),
            TimeZoneId  = definition.TimeZoneId,
            RunNumber   = identity.RunNumber,
            TaskKey     = identity.TaskKey,
            ScheduleId  = identity.ScheduleId
        };

        DateTimeOffset? answer;

        try
        {
            answer = await registry.GetNextOccurrenceAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw Transient(identity, settings.Key,
                $"The occurrence provider '{settings.Key}' failed to answer for the instant after " +
                $"{request.AfterUtc:O}", e);
        }

        if (answer is not { } slot)
        {
            retries.RecordSuccess(identity.ScheduleId);
            return null;
        }

        var utc = slot.ToUniversalTime();

        if (utc <= request.AfterUtc)
        {
            // A bug in the provider, reported as one but handled as transient: the alternative is a series
            // that dies of somebody else's arithmetic.
            logger.OccurrenceProviderBrokeContract(settings.Key, identity.ScheduleId, request.AfterUtc, utc);

            throw Transient(identity, settings.Key,
                $"The occurrence provider '{settings.Key}' answered {utc:O} for the occurrence strictly after " +
                $"{request.AfterUtc:O}: an occurrence must be strictly later than the instant it is asked " +
                "about, or it would be scheduled in the past", null);
        }

        retries.RecordSuccess(identity.ScheduleId);

        return utc;
    }

    /// <summary>
    /// Wraps a provider failure with the wait its schedule has earned, and says so once per failure.
    /// </summary>
    private OccurrenceProviderException Transient(ScheduleIdentity identity, string key, string message,
                                                  Exception? inner)
    {
        var (delay, failures) = retries.RecordFailure(identity.ScheduleId);

        logger.OccurrenceProviderFailed(inner, key, identity.ScheduleId, failures, delay);

        return new OccurrenceProviderException(key, identity.ScheduleId, message, inner)
        {
            RetryAfter          = delay,
            ConsecutiveFailures = failures
        };
    }
}

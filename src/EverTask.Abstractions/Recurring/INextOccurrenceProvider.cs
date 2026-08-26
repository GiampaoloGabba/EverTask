namespace EverTask.Abstractions;

/// <summary>
/// Answers the one question a schedule grid has to answer — "which occurrence comes after this instant" — for
/// the calendars the fluent API cannot express: business days, a holiday table, opening hours the application
/// keeps in its own database.
/// </summary>
/// <remarks>
/// <para>
/// Registered by KEY (<c>AddOccurrenceProvider&lt;T&gt;("business-days")</c>) and selected on a schedule with
/// <c>UseOccurrenceProvider(key, config)</c>. Only the key and the opaque configuration string are persisted,
/// never a type name, so renaming or moving the implementation never orphans a row.
/// </para>
/// <para>
/// It is resolved from a fresh scope for every call, so it may depend on scoped services (a DbContext, a
/// repository). Keep it fast: everything that moves a schedule forward — the dispatch, the advance after a
/// run, the recovery, a catch-up plan — waits on it.
/// </para>
/// <para>
/// A call that THROWS is treated as transient (the application's database being briefly down must not kill a
/// series): the schedule is re-parked with a backoff, nothing is written, and the next attempt asks again. A
/// call that answers at or before <see cref="NextOccurrenceRequest.AfterUtc"/> is a contract violation and is
/// reported as one.
/// </para>
/// </remarks>
public interface INextOccurrenceProvider
{
    /// <summary>
    /// The first occurrence STRICTLY after <see cref="NextOccurrenceRequest.AfterUtc"/>, in UTC, or
    /// <c>null</c> when the series has no further occurrence and should end.
    /// </summary>
    /// <param name="request">What is being asked, and about which schedule.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The answer must be strictly later than <see cref="NextOccurrenceRequest.AfterUtc"/>: an equal or
    /// earlier instant would be scheduled in the past and re-fire immediately. It is asked at arbitrary
    /// instants, not only at the ones it has already returned — a catch-up plan probes the axis to find where
    /// a backlog begins — so it must be answerable for any <c>AfterUtc</c>.
    /// </remarks>
    ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                      CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the same <see cref="NextOccurrenceRequest.AfterUtc"/> always yields the same answer, so the
    /// same grid can be walked twice and reach the same slots. Default: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// It is what <see cref="CatchUpOverflowPolicy.SkipOldest"/> needs: keeping only the most recent slots of
    /// an over-long backlog means finding where they begin by probing the instant axis, and a grid that
    /// answers differently each time cannot be probed. A schedule that asks for that policy over a provider
    /// which does not declare determinism is refused when it is dispatched, rather than replaying the wrong
    /// slots. Everything else works either way.
    /// </remarks>
    bool IsDeterministic => false;
}

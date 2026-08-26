namespace EverTask.Dispatcher;

/// <summary>
/// The outcome of a recovery dispatch that could not be made because the schedule's
/// <see cref="INextOccurrenceProvider"/> did not answer: nothing was written, and the row was parked to ask
/// again after the backoff (V4).
/// </summary>
/// <remarks>
/// It is a THIRD outcome, and it exists because the other two are both wrong about it. Returning the schedule
/// id says the dispatch happened — and startup recovery reads that as proof the row is healthy, so it clears
/// the L18 failure counter a previous restart may really have earned. Letting the provider's own exception out
/// says the dispatch FAILED, and the recovery's generic catch would count it against that same budget, ending
/// a series after a handful of restarts of a database outage — which is precisely what V4(1) forbids.
/// <para>
/// So it says neither: this restart neither proved the row healthy nor added a failure to it, and the counter
/// is left exactly as it was. It never escapes the two callers that dispatch with <c>isRecovery</c> — startup
/// recovery and the schedule-retry delivery — because it is only ever thrown on that path, after the re-park
/// has already reported itself.
/// </para>
/// </remarks>
internal sealed class ScheduleDeferredByProviderException(Guid scheduleId, OccurrenceProviderException failure)
    : Exception(
        $"Schedule {scheduleId} was not dispatched: the occurrence provider '{failure.ProviderKey}' could not " +
        $"answer ({failure.ConsecutiveFailures} consecutive failure(s)). Nothing was written and the row was " +
        "parked to ask again.", failure)
{
    /// <summary>The schedule row that was left alone.</summary>
    public Guid ScheduleId { get; } = scheduleId;
}

namespace EverTask.Dispatcher;

/// <summary>
/// The outcome of a recovery dispatch that could not be made because the schedule's
/// <see cref="INextOccurrenceProvider"/> did not answer: nothing was written, and the row was parked to ask
/// again after the backoff.
/// </summary>
/// <remarks>
/// A THIRD outcome, because the other two are both wrong about it: returning the schedule id says the dispatch
/// happened, which startup recovery reads as proof the row is healthy and clears the failure counter a previous
/// restart may really have earned; letting the provider's own exception out says the dispatch FAILED, and the
/// recovery's generic catch would count it against that same budget, ending a series over a database outage.
/// This restart neither proved the row healthy nor added a failure to it, and the counter is left as it was.
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

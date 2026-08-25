namespace EverTask.Abstractions;

/// <summary>
/// Changes a recurring schedule that is already running, without going through a dispatch (S2).
/// </summary>
/// <remarks>
/// <para>
/// Registered by <c>AddEverTask</c> alongside <see cref="ITaskDispatcher"/>, which is left untouched: a
/// dispatch registers a schedule, this manages the one already registered. Every schedule is addressed by its
/// dispatch <c>taskKey</c>, because that is the name the application already knows it by; an occurrence is
/// addressed by its own id, because it is one row of many under a schedule.
/// </para>
/// <para>
/// The store is the source of truth for every change. Each row carries a schedule version, every update
/// compares against the version it was computed from, and a run that finishes after an update loses that
/// comparison and recomputes against the new definition rather than writing its stale one. A storage without
/// that compare-and-swap is refused rather than emulated: without it a reschedule could report success while a
/// completion in flight quietly overwrote it.
/// </para>
/// <para>
/// What each call needs from the storage differs, and it is the call that says so, not the interface.
/// <see cref="Reschedule"/>, <see cref="ReevaluateSchedule"/> and <see cref="ResumeSchedule"/> rewrite a
/// schedule row and need <c>SupportsScheduleVersioning</c>. <see cref="RequeueFailedOccurrence"/> addresses a
/// row that only a durable schedule ever creates and needs <c>SupportsDurableOccurrences</c> instead.
/// <see cref="CancelSchedule"/> needs neither: it writes a cancellation, which every storage has always been
/// able to do, so a store with no capability at all can still end a series on purpose. What a call needs also
/// depends on what it is ASKED to write: a <see cref="Reschedule"/> whose new definition turns the schedule
/// durable needs <c>SupportsDurableOccurrences</c> on top of the versioning, for the same reason a dispatch
/// does — the occurrence operations have no half-atomic emulation to degrade to.
/// </para>
/// <para>
/// A reschedule is immediate for occurrences that have not fired yet. One already handed to a worker queue is
/// considered fired and may finish under the definition it started with — its advance still applies the new
/// one.
/// </para>
/// </remarks>
public interface ITaskScheduleManager
{
    /// <summary>
    /// Replaces a running schedule's definition with a freshly built one.
    /// </summary>
    /// <param name="taskKey">The dispatch key the schedule was registered under.</param>
    /// <param name="configure">The new schedule, built exactly as at dispatch.</param>
    /// <param name="mode">How the new cursor is chosen — see <see cref="RescheduleMode"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">
    /// No storage is registered; the registered one does not implement schedule versioning; or the new
    /// definition asks for durable occurrences and the storage does not implement those either.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No schedule carries that key; the row is a one-shot; the schedule was cancelled; the new definition has
    /// no occurrence left to run; or <see cref="RescheduleMode.RebaseFromCursor"/> cannot map the cursor —
    /// because the two definitions have different shapes, or because the period holds no valid slot.
    /// </exception>
    Task<ScheduleUpdateResult> Reschedule(string taskKey, Action<IRecurringTaskBuilder> configure,
                                          RescheduleMode mode = RescheduleMode.RecalculateFromNow,
                                          CancellationToken ct = default);

    /// <summary>
    /// Re-reads the schedule's current definition and recomputes its cursor from now, without changing the
    /// definition itself.
    /// </summary>
    /// <remarks>
    /// The deterministic answer to "something the schedule depends on changed" — a calendar, a feature flag, an
    /// occurrence provider's configuration — instead of waiting for a poll that does not exist.
    /// <para>
    /// It recomputes FROM NOW, which is <see cref="RescheduleMode.RecalculateFromNow"/> applied to the stored
    /// definition: on a durable schedule that is behind, the slots it still owes are passed over and reported
    /// as discarded (<see cref="ScheduleUpdateResult.DiscardedBacklog"/>, and a <c>BacklogDiscarded</c> event).
    /// To release a halted or late catch-up while KEEPING that backlog, call <see cref="ResumeSchedule"/>.
    /// </para>
    /// </remarks>
    Task<ScheduleUpdateResult> ReevaluateSchedule(string taskKey, CancellationToken ct = default);

    /// <summary>
    /// Releases a durable catch-up that halted itself and hands the schedule back to the materializer, KEEPING
    /// its cursor.
    /// </summary>
    /// <remarks>
    /// A halt is durable on purpose: neither the passage of time nor a restart clears it, so an operator sees
    /// it. Because the cursor is kept, the backlog that caused the halt is re-planned against the definition as
    /// it stands now — if it still exceeds the cap, the schedule halts again.
    /// </remarks>
    Task<ScheduleUpdateResult> ResumeSchedule(string taskKey, CancellationToken ct = default);

    /// <summary>
    /// Puts a terminal occurrence (<c>Failed</c> or <c>Cancelled</c>) back in the queue, keeping its id, its
    /// history and its audit trail.
    /// </summary>
    /// <param name="occurrenceId">The occurrence row. A schedule row is refused.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the occurrence was requeued; false when it was not terminal (any more).</returns>
    /// <exception cref="NotSupportedException">
    /// No storage is registered, or the registered one does not implement durable occurrences — the only kind
    /// of schedule that has occurrences to requeue.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No task carries that id; the row is a schedule rather than an occurrence; its schedule was cancelled,
    /// which is terminal for every row under it; or its payload cannot be rebuilt — requeuing that last one
    /// would only put a row nothing can deliver back in the queue.
    /// </exception>
    /// <remarks>
    /// Re-dispatching under the same key would delete the row and create a new one, losing exactly what an
    /// operator is looking at when they decide to retry. It spends no run of the series either: on a durable
    /// schedule the run budget counts materializations, and a requeue materializes nothing.
    /// </remarks>
    Task<bool> RequeueFailedOccurrence(Guid occurrenceId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a running schedule, and with it every occurrence of it still pending.
    /// </summary>
    /// <remarks>
    /// Occurrences already executing are left to finish. The cancellation is terminal: the schedule cannot be
    /// rescheduled afterwards, nor can one of its occurrences be requeued — it has to be dispatched again.
    /// This is the one call on this interface that asks the storage for no capability beyond being registered.
    /// </remarks>
    /// <exception cref="NotSupportedException">No storage is registered.</exception>
    /// <exception cref="InvalidOperationException">
    /// No schedule carries that key, or the row is a one-shot.
    /// </exception>
    Task CancelSchedule(string taskKey, CancellationToken ct = default);
}

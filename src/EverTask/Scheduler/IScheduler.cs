namespace EverTask.Scheduler;

public interface IScheduler
{
    void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null);

    /// <summary>
    /// Invalidates a parked registration for the given task, if present.
    /// Used when a task is re-dispatched outside the scheduler (e.g. an immediate re-dispatch
    /// via taskKey of a previously delayed task) or cancelled, so the stale parked occurrence
    /// is not executed.
    /// </summary>
    /// <param name="persistenceId">The persistence id of the task to unschedule.</param>
    /// <returns>True if a parked registration was removed.</returns>
    bool TryUnschedule(Guid persistenceId);

    /// <summary>
    /// Conditionally invalidates a parked registration: it is removed only if the currently
    /// registered executor is the <paramref name="expected"/> one. A concurrent newer
    /// registration for the same task (latest-wins) is preserved.
    /// </summary>
    /// <param name="persistenceId">The persistence id of the task to unschedule.</param>
    /// <param name="expected">The registration expected to be currently parked.</param>
    /// <returns>True if the expected registration was removed.</returns>
    /// <remarks>
    /// The default implementation returns <c>false</c> (binary compatibility for external
    /// schedulers compiled against older versions): "cannot verify the expected registration →
    /// remove nothing" is the only safe fallback, since an unconditional remove could delete a
    /// NEWER registration. Implementations should override it with a true conditional remove
    /// to preserve latest-wins semantics.
    /// </remarks>
    bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected) => false;

    /// <summary>
    /// Returns true when ANY registration is currently parked for the given task. Used by the
    /// rate-limit gate's set-then-check to distinguish "nothing is registered anymore" (our
    /// stale bookkeeping must be cleaned up) from "a newer registration took over" (it must
    /// survive untouched).
    /// </summary>
    /// <remarks>
    /// The default implementation returns <c>true</c> ("assume scheduled"), which makes
    /// external implementations skip the cleanup — conservative: at worst a parking-lot entry
    /// is reclaimed later, never a live registration damaged. Implementations should override
    /// it with a real lookup.
    /// </remarks>
    bool IsScheduled(Guid persistenceId) => true;

    /// <summary>
    /// True when <see cref="IsScheduled"/> answers from a real registry lookup rather than the
    /// conservative "assume scheduled" default.
    /// </summary>
    /// <remarks>
    /// The durable-occurrence reconciliation asks "is this occurrence still parked anywhere?" to tell a lost
    /// registration from a live one. With the default <see cref="IsScheduled"/> the answer is always yes,
    /// which is safe for the gate's cleanup but useless as evidence: a scheduler that cannot introspect must
    /// say so, and reconciliation is then disabled rather than trusting a constant. Defaults to false so an
    /// external scheduler compiled before this member is never mistaken for one that can.
    /// </remarks>
    bool SupportsScheduleInspection => false;
}

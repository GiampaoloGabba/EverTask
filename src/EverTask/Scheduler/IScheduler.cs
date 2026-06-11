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
    bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected);
}

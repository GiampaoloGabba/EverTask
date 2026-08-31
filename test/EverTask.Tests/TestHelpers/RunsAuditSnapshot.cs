using EverTask.Storage;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Defensive snapshot of a recurring series' run audits, for the assertions that run while the series
/// is still alive.
/// </summary>
public static class RunsAuditSnapshot
{
    /// <summary>
    /// A copy can lose the race to a concurrent append more than once in a row; the appends themselves
    /// are seconds apart, so a handful of immediate retries is orders of magnitude more than enough.
    /// </summary>
    private const int MaxAttempts = 16;

    /// <summary>
    /// Returns a stable copy of <paramref name="task"/>'s run audits.
    /// <para>
    /// <c>MemoryTaskStorage</c> hands out the live <see cref="QueuedTask"/> instances (its reads copy
    /// only the outer array), so <see cref="QueuedTask.RunsAudits"/> is the very list the executor
    /// appends to - under the store lock - while the series keeps running. A test body enumerates it
    /// without that lock, so on a series that is still alive it must snapshot first, exactly like
    /// <see cref="TaskWaitHelper.WaitForRecurringRunsAsync"/> does inside its poll.
    /// </para>
    /// <para>
    /// Two things can go wrong while copying, both because <c>List&lt;T&gt;.Add</c> publishes the new
    /// count before the element: the copy can see a count the (not yet replaced) backing array cannot
    /// satisfy, which throws, and it can read the slot of an in-progress append, which is still null.
    /// Retry the first, drop the second. Enumerating (LINQ, foreach) must happen on the returned array
    /// and never on the live list, which would throw "Collection was modified" instead.
    /// </para>
    /// <para>
    /// Not needed - and not used - where the series is closed by construction (MaxRuns exhausted or
    /// RunUntil expired, with the terminal status already awaited): nothing appends to it any more.
    /// </para>
    /// </summary>
    public static RunsAudit[] SnapshotRunsAudits(this QueuedTask task)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Array.FindAll(task.RunsAudits.ToArray(), audit => audit != null);
            }
            catch (ArgumentException) when (attempt < MaxAttempts)
            {
                // The list grew while it was being copied - take the snapshot again.
            }
        }
    }
}

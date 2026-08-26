namespace EverTask.Worker;

/// <summary>
/// Releases the EverTask-OWNED scope an EAGER executor carries (L27) when the enqueue boundary drops the
/// delivery before the worker ever sees it.
/// </summary>
/// <remarks>
/// <see cref="WorkerExecutor"/>'s own claim covers a delivery it CONSUMED, on every exit. What it cannot
/// cover is one that never becomes a delivery at all: an enqueue refused as a duplicate (startup recovery
/// racing a live dispatch — the very race <see cref="TaskDeliveryRegistry"/> exists to stop), a task
/// cancelled before it was written, a row that terminally finished since it was read, and a Drop* eviction.
/// Each of those drops an executor still holding a handler and every scoped dependency built with it — in a
/// real application a DbContext and its pooled connection — with nobody left to dispose them.
/// <para>
/// Only a drop that is TERMINAL for this instance releases. <see cref="EnqueueResult.QueueFull"/> and
/// <see cref="EnqueueResult.DuplicateInProcess"/> hand the executor BACK to a caller that re-parks and
/// retries the very same instance (both schedulers do), so releasing there would run a later delivery on a
/// disposed handler; the same goes for every exception path, which the schedulers treat as a full queue.
/// </para>
/// </remarks>
internal static class DroppedDelivery
{
    public static async ValueTask ReleaseAsync(TaskHandlerExecutor task, ILogger logger)
    {
        // A lazy executor owns nothing: its handler belongs to the worker's per-task scope. An executor
        // built WITHOUT a scope — never by this library, only by a caller constructing the public record
        // itself — still gets the handler instance it carries released.
        var owned = task.HandlerScope ?? task.Handler as IAsyncDisposable;

        if (owned == null)
            return;

        try
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Never fails the enqueue decision that dropped the task: the row is already where that
            // decision left it, and the only thing lost is the scope this call was trying to release.
            logger.DroppedDeliveryReleaseFailed(e, task.PersistenceId);
        }
    }
}

namespace EverTask.Worker;

/// <summary>
/// Releases the EverTask-owned scope an eager executor carries when the enqueue boundary drops a delivery the
/// worker never sees. Only a drop that is TERMINAL for this instance may release: <see cref="EnqueueResult.QueueFull"/>,
/// <see cref="EnqueueResult.DuplicateInProcess"/> and every exception path hand the SAME instance back to a
/// caller that re-parks and retries it.
/// </summary>
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

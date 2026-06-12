using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Shared status bucketing for dashboard and statistics aggregations, so every service counts
/// the same statuses the same way.
/// </summary>
internal static class TaskStatusBuckets
{
    /// <summary>
    /// A task waiting to execute: persisted but not yet delivered (<c>WaitingQueue</c>),
    /// in a worker channel or parked by the rate limiter (<c>Queued</c>), or <c>Pending</c>.
    /// </summary>
    public static bool IsPending(QueuedTaskStatus status) =>
        status is QueuedTaskStatus.WaitingQueue or QueuedTaskStatus.Pending or QueuedTaskStatus.Queued;
}

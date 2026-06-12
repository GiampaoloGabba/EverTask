namespace EverTask.Storage;

/// <summary>
/// Optional side-interface for storage providers that can compute monitoring aggregations
/// without materializing the whole backlog. Consumers (e.g. Monitor.Api services) type-check
/// their <see cref="ITaskStorage"/> for this interface and fall back to
/// <see cref="ITaskStorage.GetAll"/> when it is not implemented.
/// </summary>
/// <remarks>
/// Defined as a side-interface (rather than default interface methods on
/// <see cref="ITaskStorage"/>) so existing custom storage implementations keep compiling and
/// can opt in incrementally. Implementations should translate these calls to set-based queries
/// (GROUP BY) — the point is fixing O(backlog) reads amplified by dashboard refresh cycles.
/// </remarks>
public interface ITaskStorageStatistics
{
    /// <summary>
    /// Counts tasks grouped by status, optionally restricted to tasks created at/after
    /// <paramref name="createdAtOrAfterUtc"/>.
    /// </summary>
    Task<IReadOnlyDictionary<QueuedTaskStatus, int>> CountByStatusAsync(
        DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default);

    /// <summary>
    /// Counts tasks grouped by (queue, status), optionally restricted to tasks created at/after
    /// <paramref name="createdAtOrAfterUtc"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<QueuedTaskStatus, int>>> CountByQueueAndStatusAsync(
        DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default);
}

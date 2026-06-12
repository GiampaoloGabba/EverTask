namespace EverTask.RateLimiting;

/// <summary>
/// Snapshot of the parked tasks of one (queue, key) bucket.
/// </summary>
/// <param name="QueueName">The worker queue the parked tasks belong to (captured at park time).</param>
/// <param name="Key">The throttling key.</param>
/// <param name="ParkedCount">Number of distinct tasks currently parked for the bucket.</param>
/// <param name="NextSlotUtc">The earliest reserved slot (UTC) among the bucket's parked tasks.</param>
public readonly record struct RateLimitKeySnapshot(
    string QueueName,
    string Key,
    int ParkedCount,
    DateTimeOffset NextSlotUtc);

/// <summary>
/// Read-only introspection over the keyed rate limiter for monitoring integrations
/// (dashboards, Monitor.Api). Storage cannot distinguish a parked task from a queued one —
/// deferrals are storage-invisible — so throttling visibility comes from this in-memory view.
/// </summary>
/// <remarks>
/// The view is SINGLE-NODE: it reflects this process' limiter and scheduler only. Inject as an
/// optional dependency (absent when EverTask is not registered in the same container).
/// </remarks>
public interface IRateLimiterIntrospection
{
    /// <summary>Number of distinct rate-limited tasks currently parked waiting for budget.</summary>
    int ParkedTaskCount { get; }

    /// <summary>The configured parking-lot cap (<see cref="RateLimiterOptions.MaxParkedTasks"/>).</summary>
    int MaxParkedTasks { get; }

    /// <summary>Number of (task type, key) buckets currently tracked by the limiter.</summary>
    int TrackedKeyCount { get; }

    /// <summary>
    /// Total acquisitions that failed open because the tracked-keys cap was exceeded
    /// (<see cref="RateLimiterOptions.MaxTrackedKeys"/>).
    /// </summary>
    long FailOpenCount { get; }

    /// <summary>Parked counts and next slots per (queue, key).</summary>
    IReadOnlyList<RateLimitKeySnapshot> GetParkedSnapshot();

    /// <summary>
    /// The reserved slot of a parked task (UTC), or null when the task is not currently parked.
    /// Used for the per-task <c>throttledUntil</c> overlay (in-memory join, single-node).
    /// </summary>
    DateTimeOffset? GetThrottledUntil(Guid taskId);
}

/// <summary>
/// Default introspection over the in-memory parking lot and limiter.
/// </summary>
internal sealed class RateLimiterIntrospection(
    RateLimitParkingLot parkingLot,
    IKeyedRateLimiter limiter) : IRateLimiterIntrospection
{
    public int ParkedTaskCount => parkingLot.Count;

    public int MaxParkedTasks => parkingLot.MaxParkedTasks;

    public int TrackedKeyCount => limiter is InMemoryKeyedRateLimiter inMemory ? inMemory.TrackedKeyCount : 0;

    public long FailOpenCount => limiter is InMemoryKeyedRateLimiter inMemory ? inMemory.FailOpenCount : 0;

    public IReadOnlyList<RateLimitKeySnapshot> GetParkedSnapshot() =>
        parkingLot.GetSnapshot()
                  .Select(s => new RateLimitKeySnapshot(s.QueueName, s.Key, s.ParkedCount, s.NextSlotUtc))
                  .ToList();

    public DateTimeOffset? GetThrottledUntil(Guid taskId) => parkingLot.GetSlot(taskId);
}

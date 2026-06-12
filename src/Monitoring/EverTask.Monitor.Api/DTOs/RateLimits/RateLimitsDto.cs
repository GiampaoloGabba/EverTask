namespace EverTask.Monitor.Api.DTOs.RateLimits;

/// <summary>
/// Parked tasks of one (queue, key) rate-limit bucket.
/// </summary>
/// <param name="QueueName">The worker queue the parked tasks belong to.</param>
/// <param name="Key">The throttling key (e.g. a tenant id).</param>
/// <param name="ParkedCount">Number of distinct tasks parked for the bucket.</param>
/// <param name="NextSlotUtc">The earliest reserved slot (UTC) among the bucket's parked tasks.</param>
public record RateLimitKeyDto(
    string QueueName,
    string Key,
    int ParkedCount,
    DateTimeOffset NextSlotUtc
);

/// <summary>
/// Snapshot of the keyed rate limiter state.
/// </summary>
/// <param name="Enabled">
/// False when no rate-limiter introspection is available (EverTask not registered in the same
/// container, e.g. standalone API mode): all other values are zero/empty.
/// </param>
/// <param name="ThrottledTasks">Number of rate-limited tasks currently parked waiting for budget.</param>
/// <param name="MaxParkedTasks">The configured parking-lot cap.</param>
/// <param name="TrackedKeys">Number of (task type, key) buckets tracked by the limiter.</param>
/// <param name="FailOpenCount">Total acquisitions that failed open due to the tracked-keys cap.</param>
/// <param name="Keys">Per-(queue, key) parked counts and next slots.</param>
/// <remarks>
/// The view is in-memory and SINGLE-NODE: it reflects this process' limiter and scheduler only.
/// </remarks>
public record RateLimitsDto(
    bool Enabled,
    int ThrottledTasks,
    int MaxParkedTasks,
    int TrackedKeys,
    long FailOpenCount,
    List<RateLimitKeyDto> Keys
);

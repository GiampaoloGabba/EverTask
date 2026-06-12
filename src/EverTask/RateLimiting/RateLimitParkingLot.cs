using System.Collections.Concurrent;

namespace EverTask.RateLimiting;

/// <summary>
/// L2 bound of the rate limiter: counts the DISTINCT gated tasks currently parked in the
/// in-memory scheduler waiting for budget. When the count reaches
/// <see cref="RateLimiterOptions.MaxParkedTasks"/>, consumers of the affected queues pause
/// draining: the bounded channel fills up and the native <c>FullMode=Wait</c> backpressure
/// reaches producers. Safety valve, not normal operation.
/// </summary>
/// <remarks>
/// Accounting rules (owner decision): increment on FIRST park of a task; decrement only when
/// the task re-enters a worker channel (enqueue notification), is dropped, or cancelled — never
/// on a full-queue re-park. The consumer pause is bounded (<see cref="MaxOverflowPause"/> per
/// task) so an adversarial interleaving (channel full of fresh dispatches while the lot is at
/// cap) degrades to slow progress instead of wedging the pipeline.
/// </remarks>
internal sealed class RateLimitParkingLot(RateLimiterOptions options)
{
    internal readonly record struct ParkedTaskInfo(string QueueName, string Key, DateTimeOffset SlotUtc);

    private readonly ConcurrentDictionary<Guid, ParkedTaskInfo> _parked = new();
    private readonly ConcurrentDictionary<string, int> _perQueueCounts = new();
    private int _count;

    /// <summary>Poll interval while a consumer is paused on an over-capacity lot. Internal for testing purposes.</summary>
    internal TimeSpan OverflowPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Upper bound of a single consumer pause: after this, the consumer proceeds anyway (the lot
    /// may transiently overshoot the cap by a trickle) so the pipeline can never wedge.
    /// Internal for testing purposes.
    /// </summary>
    internal TimeSpan MaxOverflowPause { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Number of distinct parked tasks.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The configured cap (resolved by AddEverTask).</summary>
    public int MaxParkedTasks => options.MaxParkedTasks;

    /// <summary>
    /// Registers a parked task (idempotent: a re-park of the same task refreshes its slot info
    /// without incrementing the count).
    /// </summary>
    public void Park(Guid taskId, string queueName, string key, DateTimeOffset slotUtc)
    {
        var info = new ParkedTaskInfo(queueName, key, slotUtc);

        if (_parked.TryAdd(taskId, info))
        {
            Interlocked.Increment(ref _count);
            _perQueueCounts.AddOrUpdate(queueName, 1, static (_, current) => current + 1);
        }
        else
        {
            _parked[taskId] = info;
        }
    }

    /// <summary>
    /// Unregisters a parked task (enqueue notification, drop or cancel). Idempotent.
    /// </summary>
    public void Remove(Guid taskId)
    {
        if (!_parked.TryRemove(taskId, out var info))
            return;

        Interlocked.Decrement(ref _count);
        _perQueueCounts.AddOrUpdate(info.QueueName, 0, static (_, current) => current - 1);
    }

    /// <summary>
    /// Called by worker queues when a task enters a channel: a parked task that re-entered a
    /// queue is no longer parked. No-op for tasks that were never parked.
    /// </summary>
    public void OnTaskEnqueued(Guid taskId) => Remove(taskId);

    /// <summary>
    /// Pauses the calling consumer while the lot is over capacity AND the given queue hosts
    /// parked tasks. Bounded by <see cref="MaxOverflowPause"/>; cheap fast path when under cap.
    /// </summary>
    public async ValueTask WaitForCapacityAsync(string queueName, CancellationToken ct)
    {
        if (Count < MaxParkedTasks)
            return;

        var deadline = DateTimeOffset.UtcNow + MaxOverflowPause;

        while (Count >= MaxParkedTasks
               && _perQueueCounts.TryGetValue(queueName, out var queueCount) && queueCount > 0
               && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(OverflowPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Snapshot for monitoring: parked counts and earliest slot per (queue, key).
    /// </summary>
    public IReadOnlyList<(string QueueName, string Key, int ParkedCount, DateTimeOffset NextSlotUtc)> GetSnapshot()
    {
        return _parked.Values
                      .GroupBy(p => (p.QueueName, p.Key))
                      .Select(g => (g.Key.QueueName, g.Key.Key, g.Count(), g.Min(p => p.SlotUtc)))
                      .ToList();
    }
}

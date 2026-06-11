namespace EverTask.RateLimiting;

/// <summary>
/// Global infrastructure knobs for the keyed rate limiter. Configure via
/// <c>EverTaskServiceConfiguration.SetRateLimiterOptions</c>; per-task-type limits are declared
/// on handlers through <see cref="RateLimitPolicy"/>.
/// </summary>
/// <example>
/// <code>
/// services.AddEverTask(opt => opt
///     .RegisterTasksFromAssembly(typeof(Program).Assembly)
///     .SetRateLimiterOptions(o =>
///     {
///         o.MaxParkedTasks     = 5000;
///         o.MaxTrackedKeys     = 100_000;
///         o.MaxKeyLength       = 256;
///         o.EmitDeferralEvents = true;
///     }));
/// </code>
/// </example>
public sealed class RateLimiterOptions
{
    private int _maxParkedTasks;

    /// <summary>
    /// Gets or sets the maximum number of DISTINCT rate-limited tasks parked in the in-memory
    /// scheduler waiting for budget. When the cap is reached, consumers pause draining the
    /// affected queue until the count drops below the cap, so the bounded channel fills up and
    /// native backpressure reaches producers. Safety valve, not normal operation.
    /// Default: <c>min(5000, 2 × default-queue channel capacity)</c>.
    /// </summary>
    public int MaxParkedTasks
    {
        get => _maxParkedTasks;
        set => _maxParkedTasks = value;
    }

    /// <summary>
    /// Gets or sets the maximum number of (task type, key) buckets tracked in memory.
    /// When exceeded, acquisitions for NEW keys fail open (the task executes without
    /// throttling) with a rate-limited warning and a monitoring event: an unbounded key space
    /// (e.g. bot-generated tenants) must not exhaust process memory.
    /// Default: 100,000.
    /// </summary>
    public int MaxTrackedKeys { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the maximum rate-limit key length. Longer keys are hashed (SHA-256) before
    /// being used as bucket identifiers, bounding per-key memory while preserving uniqueness.
    /// Default: 256 characters.
    /// </summary>
    public int MaxKeyLength { get; set; } = 256;

    /// <summary>
    /// Gets or sets whether deferral monitoring events are published. Events are aggregated at
    /// the source (first deferral per key per window plus periodic summaries) to avoid event
    /// storms under sustained throttling; per-deferral details are logged at Debug level.
    /// Default: true.
    /// </summary>
    public bool EmitDeferralEvents { get; set; } = true;

    /// <summary>
    /// Resolves computed defaults that depend on other configuration (called by AddEverTask
    /// once queues are configured).
    /// </summary>
    internal void ResolveDefaults(int defaultQueueChannelCapacity)
    {
        if (_maxParkedTasks <= 0)
            _maxParkedTasks = Math.Min(5000, 2 * defaultQueueChannelCapacity);
    }
}

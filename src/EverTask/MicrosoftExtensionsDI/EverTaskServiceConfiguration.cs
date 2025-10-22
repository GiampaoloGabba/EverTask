using EverTask.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

public class EverTaskServiceConfiguration
{
    internal BoundedChannelOptions ChannelOptions = new(GetDefaultChannelCapacity())
    {
        FullMode = BoundedChannelFullMode.Wait
    };

    internal int MaxDegreeOfParallelism = GetDefaultParallelism();

    internal bool ThrowIfUnableToPersist = true;
    internal List<Assembly> AssembliesToRegister { get; } = new();

    internal IRetryPolicy DefaultRetryPolicy { get; set; } = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500));

    internal TimeSpan? DefaultTimeout { get; set; } = null;

    /// <summary>
    /// Configuration for individual queues. The "default" queue is always present.
    /// Additional queues can be configured for workload isolation.
    /// </summary>
    internal Dictionary<string, QueueConfiguration> Queues { get; } = new();

    internal int? ShardedSchedulerShardCount { get; private set; }

    /// <summary>
    /// Enable lazy handler resolution for scheduled and recurring tasks.
    /// When enabled, handler instances are disposed after dispatch validation
    /// and recreated at execution time, reducing memory footprint.
    /// Default: true
    /// </summary>
    public bool UseLazyHandlerResolution { get; set; } = true;

    /// <summary>
    /// Time threshold for lazy handler resolution.
    /// Tasks delayed longer than this threshold will use lazy mode.
    /// Tasks delayed less than this will use eager mode (execute soon anyway).
    /// Default: 1 hour
    /// Minimum: 1 minute
    /// </summary>
    public TimeSpan LazyHandlerResolutionThreshold { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Always use lazy handler resolution for recurring tasks.
    /// Recommended to keep true to avoid long-lived handler instances.
    /// Default: true
    /// </summary>
    public bool AlwaysLazyForRecurring { get; set; } = true;

    public EverTaskServiceConfiguration SetChannelOptions(int capacity)
    {
        ChannelOptions.Capacity = capacity;
        return this;
    }

    public EverTaskServiceConfiguration SetChannelOptions(BoundedChannelOptions options)
    {
        ChannelOptions = options;
        return this;
    }

    public EverTaskServiceConfiguration SetMaxDegreeOfParallelism(int parallelism)
    {
        MaxDegreeOfParallelism = parallelism;
        return this;
    }

    public EverTaskServiceConfiguration SetThrowIfUnableToPersist(bool value)
    {
        ThrowIfUnableToPersist = value;
        return this;
    }

    public EverTaskServiceConfiguration SetDefaultRetryPolicy(IRetryPolicy policy)
    {
        DefaultRetryPolicy = policy;
        return this;
    }

    public EverTaskServiceConfiguration SetDefaultTimeout(TimeSpan? timeout)
    {
        DefaultTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Register various EverTask handlers from assembly
    /// </summary>
    /// <param name="assembly">Assembly to scan</param>
    /// <returns>This</returns>
    public EverTaskServiceConfiguration RegisterTasksFromAssembly(Assembly assembly)
    {
        AssembliesToRegister.Add(assembly);
        return this;
    }

    /// <summary>
    /// Register various EverTask handlers from assemblies
    /// </summary>
    /// <param name="assemblies">Assemblies to scan</param>
    /// <returns>This</returns>
    public EverTaskServiceConfiguration RegisterTasksFromAssemblies(
        params Assembly[] assemblies)
    {
        AssembliesToRegister.AddRange(assemblies);
        return this;
    }

    /// <summary>
    /// Sets whether to use lazy handler resolution for scheduled and recurring tasks.
    /// When enabled, handler instances are disposed after dispatch and recreated at execution.
    /// </summary>
    /// <param name="enabled">True to enable lazy resolution (default), false to disable</param>
    /// <returns>The configuration instance for method chaining</returns>
    public EverTaskServiceConfiguration SetUseLazyHandlerResolution(bool enabled)
    {
        UseLazyHandlerResolution = enabled;
        return this;
    }

    /// <summary>
    /// Sets the time threshold for lazy handler resolution.
    /// Tasks delayed longer than this will use lazy mode.
    /// </summary>
    /// <param name="threshold">Time threshold (minimum: 1 minute)</param>
    /// <returns>The configuration instance for method chaining</returns>
    /// <exception cref="ArgumentException">Thrown if threshold is less than 1 minute</exception>
    public EverTaskServiceConfiguration SetLazyHandlerResolutionThreshold(TimeSpan threshold)
    {
        if (threshold < TimeSpan.FromMinutes(1))
            throw new ArgumentException("Lazy handler resolution threshold must be at least 1 minute", nameof(threshold));

        LazyHandlerResolutionThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets whether recurring tasks always use lazy handler resolution.
    /// </summary>
    /// <param name="enabled">True to always use lazy for recurring (default), false to use threshold</param>
    /// <returns>The configuration instance for method chaining</returns>
    public EverTaskServiceConfiguration SetAlwaysLazyForRecurring(bool enabled)
    {
        AlwaysLazyForRecurring = enabled;
        return this;
    }

    /// <summary>
    /// Enables high-performance sharded scheduler for workloads exceeding 10k Schedule() calls/sec.
    /// Each shard runs independently with its own timer and priority queue, reducing lock contention.
    /// </summary>
    /// <param name="shardCount">
    /// Number of independent scheduler shards.
    /// Default: 0 (auto-scales to Environment.ProcessorCount with minimum 4).
    /// Recommended: 4-16 shards for most workloads.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// Use this when:
    /// - Sustained load > 10k Schedule() calls/sec
    /// - Burst spikes > 20k Schedule() calls/sec
    /// - 100k+ tasks scheduled concurrently
    ///
    /// Trade-offs:
    /// - PRO: 2-4x throughput improvement, better spike handling
    /// - PRO: Complete failure isolation between shards
    /// - CON: ~300 bytes additional memory overhead per shard
    /// - CON: Additional background threads (1 per shard)
    /// </remarks>
    public EverTaskServiceConfiguration UseShardedScheduler(int shardCount = 0)
    {
        ShardedSchedulerShardCount = shardCount;
        return this;
    }

    /// <summary>
    /// Calculate default channel capacity based on CPU cores.
    /// Scales with available processors for optimal throughput.
    /// </summary>
    /// <returns>Default channel capacity (minimum 1000)</returns>
    private static int GetDefaultChannelCapacity()
    {
        // Scale con CPU cores
        int cores = Environment.ProcessorCount;
        return Math.Max(1000, cores * 200); // Min 1000, ~1600 su 8-core
    }

    /// <summary>
    /// Calculate default max degree of parallelism based on CPU cores.
    /// Conservative default optimized for I/O-bound tasks.
    /// </summary>
    /// <returns>Default max degree of parallelism (minimum 4)</returns>
    private static int GetDefaultParallelism()
    {
        // Conservative: cores * 2 (buono per I/O-bound tasks)
        int cores = Environment.ProcessorCount;
        return Math.Max(4, cores * 2); // Min 4, ~16 su 8-core
    }
}

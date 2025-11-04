using EverTask.Abstractions;
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
    /// Enable adaptive lazy handler resolution for scheduled and recurring tasks.
    /// When enabled, handlers are recreated at execution time based on task scheduling:
    /// - Recurring tasks with intervals >= 5 minutes use lazy mode (memory efficient)
    /// - Recurring tasks with intervals < 5 minutes use eager mode (performance efficient)
    /// - Delayed tasks with delay >= 30 minutes use lazy mode
    /// - Delayed tasks with delay < 30 minutes use eager mode
    /// Default: true
    /// </summary>
    public bool UseLazyHandlerResolution { get; set; } = true;

    /// <summary>
    /// Configuration for persistent handler logging.
    /// When enabled, logs written via Logger property in handlers are stored in database for audit trails.
    /// Logs are ALWAYS forwarded to ILogger infrastructure regardless of this setting.
    /// </summary>
    public PersistentLoggerOptions PersistentLogger { get; } = new();

    internal AuditLevel DefaultAuditLevel { get; private set; } = AuditLevel.Full;

    internal AuditRetentionPolicy? RetentionPolicy { get; private set; }

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
    /// Disables lazy handler resolution completely.
    /// Use only if lazy mode causes issues in your environment.
    /// </summary>
    /// <returns>The configuration instance for method chaining</returns>
    public EverTaskServiceConfiguration DisableLazyHandlerResolution()
    {
        UseLazyHandlerResolution = false;
        return this;
    }

    /// <summary>
    /// Configures persistent handler logging options.
    /// Automatically enables database persistence - logs written via Logger property in handlers are stored in database for audit trails.
    /// Logs are ALWAYS forwarded to ILogger infrastructure (console, file, Serilog) regardless of this setting.
    /// </summary>
    /// <param name="configure">Action to configure persistent logger options</param>
    /// <returns>The configuration instance for method chaining</returns>
    public EverTaskServiceConfiguration WithPersistentLogger(Action<PersistentLoggerOptions> configure)
    {
        PersistentLogger.Enabled = true; // Auto-enable when WithPersistentLogger is called
        configure(PersistentLogger);
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
    /// Sets the default audit level for all task handlers.
    /// Individual handlers can override this using the <see cref="AuditLevelAttribute"/> attribute.
    /// </summary>
    /// <param name="auditLevel">
    /// The default audit level to apply.
    /// - Full: Complete audit trail with all status transitions (default, backward compatible)
    /// - Minimal: Only errors and last execution timestamp (optimized for high-frequency tasks)
    /// - ErrorsOnly: Only failed executions are audited
    /// - None: No audit trail, only QueuedTask table updated
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// Use lower audit levels (Minimal/ErrorsOnly/None) for high-frequency recurring tasks
    /// to prevent database bloat. For example, a task running every 5 minutes generates
    /// 1,152 audit records/day with Full audit level, but 0 records with Minimal (if successful).
    /// </remarks>
    public EverTaskServiceConfiguration SetDefaultAuditLevel(AuditLevel auditLevel)
    {
        DefaultAuditLevel = auditLevel;
        return this;
    }

    /// <summary>
    /// Configures automatic audit trail retention policy.
    /// Retention is enforced by the optional <see cref="AuditCleanupHostedService"/>.
    /// </summary>
    /// <param name="retentionPolicy">
    /// The retention policy to apply. Set to null to disable retention (default).
    /// Use <see cref="AuditRetentionPolicy.WithUniformRetention"/> for simple TTL or
    /// <see cref="AuditRetentionPolicy.WithErrorPriority"/> to keep errors longer.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// Retention policy requires registering the cleanup service:
    /// <code>
    /// services.AddEverTask(opt => opt.SetAuditRetentionPolicy(
    ///     AuditRetentionPolicy.WithErrorPriority(successRetentionDays: 7, errorRetentionDays: 90)
    /// )).AddAuditCleanup(cleanupIntervalHours: 24);
    /// </code>
    /// </remarks>
    public EverTaskServiceConfiguration SetAuditRetentionPolicy(AuditRetentionPolicy? retentionPolicy)
    {
        RetentionPolicy = retentionPolicy;
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

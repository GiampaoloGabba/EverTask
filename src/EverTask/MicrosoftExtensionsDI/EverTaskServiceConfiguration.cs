using EverTask.Configuration;
using EverTask.RateLimiting;
using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

// EverTask's DI wiring lives in Microsoft.Extensions.DependencyInjection so it surfaces without
// extra usings, per the .NET hosting-extensions convention.
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public class EverTaskServiceConfiguration
{
    internal BoundedChannelOptions ChannelOptions = new(GetDefaultChannelCapacity())
    {
        FullMode = BoundedChannelFullMode.Wait
    };

    internal int MaxDegreeOfParallelism = GetDefaultParallelism();

    internal bool ThrowIfUnableToPersist = true;
    internal List<Assembly> AssembliesToRegister { get; } = [];

    internal IRetryPolicy DefaultRetryPolicy { get; set; } = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500));

    internal TimeSpan? DefaultTimeout { get; set; }

    /// <summary>
    /// Configuration for individual queues. The "default" queue is always present.
    /// Additional queues can be configured for workload isolation.
    /// </summary>
    internal Dictionary<string, QueueConfiguration> Queues { get; } = new();

    internal int? ShardedSchedulerShardCount { get; private set; }

    /// <summary>
    /// Diagnostics collected during handler assembly scanning (duplicate closed handlers,
    /// unsupported open-generic handlers). Logged once at startup by <c>WorkerService</c>.
    /// </summary>
    internal List<string> HandlerRegistrationWarnings { get; } = [];

    /// <summary>
    /// Enable adaptive lazy handler resolution.
    /// When enabled, handlers are recreated at execution time based on task scheduling:
    /// - Immediate tasks use lazy mode (the worker resolves a fresh handler in its per-task scope;
    ///   an eager instance resolved at dispatch would be pinned in the root container until shutdown)
    /// - Recurring tasks with intervals &gt;= 5 minutes use lazy mode (memory efficient)
    /// - Recurring tasks with intervals &lt; 5 minutes use eager mode (performance efficient)
    /// - Delayed tasks with delay &gt;= 30 minutes use lazy mode
    /// - Delayed tasks with delay &lt; 30 minutes use eager mode
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

    internal RateLimiterOptions RateLimiterOptions { get; } = new();

    internal TimeSpan MisfireThreshold { get; private set; } = TimeSpan.FromSeconds(5);

    internal string? DefaultScheduleTimeZoneId { get; private set; }

    private int? _materializationConcurrency;

    /// <summary>
    /// How many durable schedules may materialize occurrences at the same time. Resolved lazily against
    /// <see cref="MaxDegreeOfParallelism"/> so it follows a parallelism configured after this one.
    /// </summary>
    /// <remarks>
    /// Clamped to at least one for the same reason the worker and startup recovery clamp it:
    /// <see cref="SetMaxDegreeOfParallelism"/> accepts zero and negatives, and the two places that consume it
    /// treat those as "one". Inheriting the raw value instead made a zero throw out of the materializer's own
    /// constructor — on the schedule's first slot, after the scheduler had already consumed its registration,
    /// so nothing re-parked the row and every restart repeated it.
    /// </remarks>
    internal int MaterializationConcurrency => Math.Max(1, _materializationConcurrency ?? MaxDegreeOfParallelism);

    internal TimeSpan BacklogRetryInterval { get; private set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The occurrence providers registered with <c>AddOccurrenceProvider&lt;T&gt;(key)</c>, by key. A schedule
    /// persists the key alone, so this is what turns it back into an implementation (V2).
    /// </summary>
    /// <remarks>
    /// Ordinal comparison: the key is an identifier the application chooses and a row carries verbatim, so
    /// "Business-Days" and "business-days" are two keys — a culture-sensitive match would resolve a row to a
    /// provider its author did not name.
    /// </remarks>
    internal Dictionary<string, Type> OccurrenceProviders { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, ScheduleExclusions> ScheduleCalendars { get; } = new(StringComparer.Ordinal);

    /// <summary>How long a schedule waits before asking a failed occurrence provider again (V4).</summary>
    internal OccurrenceProviderRetryOptions OccurrenceProviderRetry { get; } = new();

    /// <summary>
    /// Upper bound of <see cref="SetBacklogRetryInterval"/>. The interval is added to a UTC instant on every
    /// operational re-park, so it has to stay inside what that addition can represent.
    /// </summary>
    internal static readonly TimeSpan MaxBacklogRetryInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Sets the channel capacity for the default queue.
    /// This determines the maximum number of tasks that can be queued in memory before backpressure is applied.
    /// </summary>
    /// <param name="capacity">
    /// Maximum number of tasks to queue in memory.
    /// Recommended values:
    /// - Small projects / low throughput: 500-1000
    /// - Medium projects / moderate spikes: 2000-5000
    /// - High-throughput / large spikes: 10000+
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// When the channel is full, the dispatcher will block (backpressure) until space becomes available.
    /// Tasks are persisted to storage before entering the channel, ensuring no data loss during traffic spikes.
    /// </remarks>
    public EverTaskServiceConfiguration SetChannelOptions(int capacity)
    {
        ChannelOptions.Capacity = capacity;
        return this;
    }

    /// <summary>
    /// Sets advanced channel options for the default queue.
    /// Allows fine-grained control over channel behavior (full mode, continuations, reader/writer settings).
    /// </summary>
    /// <param name="options">Fully configured <see cref="BoundedChannelOptions"/> instance.</param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// Use this overload when you need to customize:
    /// - FullMode (Wait, DropWrite, DropOldest)
    /// - SingleReader/SingleWriter optimizations
    /// - AllowSynchronousContinuations behavior
    /// For simple capacity changes, use <see cref="SetChannelOptions(int)"/> instead.
    /// </remarks>
    public EverTaskServiceConfiguration SetChannelOptions(BoundedChannelOptions options)
    {
        ChannelOptions = options;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of tasks that can execute concurrently.
    /// </summary>
    /// <param name="parallelism">
    /// Maximum concurrent tasks.
    /// Recommended values:
    /// - CPU-bound tasks: Environment.ProcessorCount
    /// - I/O-bound tasks: Environment.ProcessorCount * 2-4
    /// - Mixed workloads: Use separate queues with different parallelism settings
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// Higher parallelism increases throughput for I/O-bound tasks (API calls, database queries, file operations).
    /// For CPU-intensive tasks, avoid exceeding Environment.ProcessorCount to prevent thread contention.
    /// </remarks>
    public EverTaskServiceConfiguration SetMaxDegreeOfParallelism(int parallelism)
    {
        MaxDegreeOfParallelism = parallelism;
        return this;
    }

    /// <summary>
    /// Sets whether to throw an exception if a task cannot be persisted to storage.
    /// </summary>
    /// <param name="value">
    /// True (default): Throw exception if persistence fails, preventing task loss at the cost of request failure.
    /// False: Silently fail persistence, allowing the request to succeed but risking task loss on restart.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// Recommended: Keep this true (default) to prevent silent data loss.
    /// </para>
    /// <para>
    /// Set to false only when:
    /// - Using in-memory storage for testing
    /// - Task execution is truly optional and best-effort
    /// - You have external monitoring to detect persistence failures
    /// </para>
    /// </remarks>
    public EverTaskServiceConfiguration SetThrowIfUnableToPersist(bool value)
    {
        ThrowIfUnableToPersist = value;
        return this;
    }

    /// <summary>
    /// Sets the default retry policy for all tasks.
    /// Individual handlers can override this via the <see cref="IEverTaskHandler{T}.RetryPolicy"/> property.
    /// </summary>
    /// <param name="policy">
    /// Retry policy instance (built-in <see cref="LinearRetryPolicy"/> or
    /// <see cref="ExponentialRetryPolicy"/>, or a custom <see cref="IRetryPolicy"/>
    /// implementation, e.g. a trivial run-once policy to disable retries).
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// Default: LinearRetryPolicy with 3 retries and 500ms delay between attempts.
    /// </para>
    /// <para>
    /// Retry policies support exception filtering (v1.6.0+) to avoid retrying non-transient failures.
    /// </para>
    /// </remarks>
    public EverTaskServiceConfiguration SetDefaultRetryPolicy(IRetryPolicy policy)
    {
        DefaultRetryPolicy = policy;
        return this;
    }

    /// <summary>
    /// Sets the default timeout for task execution.
    /// Individual handlers can override this via the <see cref="IEverTaskHandler{T}.Timeout"/> property.
    /// </summary>
    /// <param name="timeout">
    /// Maximum execution time. Null (default) means no timeout.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// When a task exceeds the timeout, it is cancelled via <see cref="CancellationToken"/>.
    /// If the handler ignores the cancellation token, the task may continue running.
    /// </para>
    /// <para>
    /// Recommended timeout values:
    /// - API calls: 30 seconds - 2 minutes
    /// - Database queries: 1-5 minutes
    /// - File processing: 5-30 minutes
    /// - Long-running batch jobs: 1+ hours
    /// </para>
    /// <para>
    /// Timeout exceptions (TimeoutException) are NOT retried by default.
    /// Use custom retry policies with exception filtering if needed.
    /// </para>
    /// </remarks>
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
        // Fail at the configuration boundary: a null slipping into AssembliesToRegister would only
        // surface later as a NullReferenceException inside the assembly scan.
        ArgumentNullException.ThrowIfNull(assembly);

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
        ArgumentNullException.ThrowIfNull(assemblies);
        // NRT annotations are not enforced at runtime: a caller can still pass a null element.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (assemblies.Any(assembly => assembly is null))
            throw new ArgumentException("The assemblies array contains a null element.", nameof(assemblies));

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
    /// A single dispatch can override it through the <c>auditLevel</c> parameter of
    /// <c>ITaskDispatcher.Dispatch(...)</c>; the persisted value then travels with the task and is
    /// restored on recovery.
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
    /// Sets the lateness threshold used to classify delivery and durable-occurrence misfire metadata.
    /// </summary>
    /// <param name="threshold">
    /// The tolerance between a nominal slot and the time it is observed. Default: 5 seconds. Zero classifies
    /// every delivery that starts after its slot, and every overdue durable slot, as a misfire.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the threshold is negative.</exception>
    /// <remarks>
    /// <para>
    /// This is a classification threshold: it decides what
    /// <see cref="ITaskExecutionContext.Misfire"/> reports to a handler and whether the durable planner stamps
    /// a materialized row with catch-up or fire-once misfire metadata. It is not an execution gate: a late
    /// occurrence runs exactly as it did before. A backlog containing more than one due slot is always missed
    /// work, regardless of this threshold, and the one-second tolerance the recurring skip-forward path uses
    /// to avoid treating a just-scheduled occurrence as past is a separate, untouched rule.
    /// </para>
    /// <para>
    /// Raise it for schedules whose handler does not care about seconds; lower it when a handler compensates
    /// for lateness (skipping stale work, shortening a window) and needs to know sooner.
    /// </para>
    /// </remarks>
    public EverTaskServiceConfiguration SetMisfireThreshold(TimeSpan threshold)
    {
        if (threshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold,
                "The misfire threshold cannot be negative.");
        }

        MisfireThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets how many durable schedules may be materializing occurrences at the same time.
    /// </summary>
    /// <param name="concurrency">
    /// Maximum concurrent materializations. Default: the same value as
    /// <see cref="SetMaxDegreeOfParallelism"/>, which is also what bounds startup recovery.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Less than one.</exception>
    /// <remarks>
    /// Materialization is a short burst of storage writes, so this bounds how much of that the store sees at
    /// once — it has nothing to do with how many occurrences RUN concurrently, which is the queue's
    /// parallelism, nor with how many may be alive per schedule, which is
    /// <see cref="CatchUpOptions.MaxPendingOccurrences"/>. Lower it when a large restart backlog puts more
    /// pressure on the database than the workload it is catching up on.
    /// </remarks>
    public EverTaskServiceConfiguration SetMaterializationConcurrency(int concurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        _materializationConcurrency = concurrency;
        return this;
    }

    /// <summary>
    /// Sets how long a durable schedule waits before trying again when it could not make progress: its
    /// concurrency budget was full, or a compare-and-swapped write lost its race.
    /// </summary>
    /// <param name="interval">
    /// The retry interval. Default: one minute. At least one second, and at most a day.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Below one second, or above one day.</exception>
    /// <remarks>
    /// The ordinary way a blocked schedule resumes is the kick each occurrence gives when it ends, which is
    /// immediate. This is the guarantee behind it: startup recovery runs once, so without a retry a schedule
    /// whose kick was lost would wait for the next restart. Shorter than the scheduler's own one-second tick
    /// buys nothing. A HALTED catch-up is not retried at all — it never releases itself, and only an explicit
    /// resume or reschedule clears the marker.
    /// </remarks>
    public EverTaskServiceConfiguration SetBacklogRetryInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval,
                "The backlog retry interval must be at least one second: the scheduler itself ticks once a " +
                "second, so anything shorter only adds churn.");
        }

        // An upper bound because this is the LAST resort of a blocked schedule, and the value is added to a
        // UTC instant on every re-park: an interval measured in centuries overflows that addition, and the
        // failure path re-parks by repeating exactly the same addition, so the schedule ends up parked
        // nowhere at all.
        if (interval > MaxBacklogRetryInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval,
                "The backlog retry interval must be at most one day: it is the guarantee that a blocked " +
                "schedule makes progress without a restart, and a longer one is indistinguishable from none.");
        }

        BacklogRetryInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets how long a schedule waits before asking its <see cref="INextOccurrenceProvider"/> again, when the
    /// provider could not answer.
    /// </summary>
    /// <param name="configure">Action to configure the backoff (initial and maximum).</param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Either bound is not positive, or is longer than a day.</exception>
    /// <remarks>
    /// A provider failure is treated as transient — the database a calendar is read from being briefly down
    /// must not end a series — so the schedule writes nothing, keeps its cursor and is parked again after this
    /// wait. It doubles at each consecutive failure of the same schedule, up to <c>MaxBackoff</c>, and one
    /// answer resets it.
    /// <code>
    /// opt.SetOccurrenceProviderRetry(r =>
    /// {
    ///     r.InitialBackoff = TimeSpan.FromSeconds(30);
    ///     r.MaxBackoff     = TimeSpan.FromMinutes(5);
    /// });
    /// </code>
    /// </remarks>
    public EverTaskServiceConfiguration SetOccurrenceProviderRetry(Action<OccurrenceProviderRetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(OccurrenceProviderRetry);
        return this;
    }

    /// <summary>Registers a reusable set of recurring-schedule exclusions under a persisted name.</summary>
    /// <param name="name">Case-sensitive name used by <c>ExceptCalendar(name)</c>.</param>
    /// <param name="configure">Adds the calendar's days, dates and absolute windows.</param>
    /// <returns>The configuration instance for chaining.</returns>
    /// <exception cref="ArgumentException">The trimmed name is empty or longer than 100 characters.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The name is already registered, the callback adds nothing, or the calendar exceeds exclusion limits.
    /// </exception>
    public EverTaskServiceConfiguration AddScheduleCalendar(string name, Action<IExclusionBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        name = name.Trim();
        if (name.Length > ScheduleExclusionNormalizer.MaxCalendarNameLength)
        {
            throw new ArgumentException(
                $"A schedule calendar name cannot exceed {ScheduleExclusionNormalizer.MaxCalendarNameLength} characters.",
                nameof(name));
        }

        if (ScheduleCalendars.ContainsKey(name))
            throw new InvalidOperationException($"A schedule calendar named '{name}' is already registered.");

        var builder = new ExclusionBuilder();
        configure(builder);
        var calendar = builder.Build(null);
        if (ScheduleExclusionNormalizer.Normalize(calendar))
            throw new InvalidOperationException($"The schedule calendar '{name}' adds no exclusion.");

        ScheduleCalendars.Add(name, calendar);
        return this;
    }

    /// <summary>
    /// Sets the time zone every calendar-anchored schedule is read on when it does not name one itself.
    /// </summary>
    /// <param name="timeZone">
    /// A system time zone. Its IANA id is what gets persisted with each schedule, so a row keeps meaning the
    /// same thing after this default changes — and on a host that resolves zones differently.
    /// </param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <exception cref="ArgumentException">The zone cannot be persisted as an IANA id (a custom zone).</exception>
    /// <remarks>
    /// <para>
    /// It applies at dispatch, to schedules built through <c>Dispatch(task, r =&gt; ...)</c> that are anchored
    /// to a calendar — a time of day, a day of the week, a month selector, a cron expression — and that did
    /// not call <c>InTimeZone</c>. A plain cadence (every N seconds/minutes/hours) is never touched: it is a
    /// constant step in elapsed time, identical in every zone.
    /// </para>
    /// <para>
    /// Rows already persisted keep whatever they were dispatched with, including no zone at all: the default
    /// is stamped onto the schedule when it is built, not re-applied on recovery, so raising it does not
    /// silently move existing schedules by an hour.
    /// </para>
    /// </remarks>
    public EverTaskServiceConfiguration SetDefaultScheduleTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        DefaultScheduleTimeZoneId = ScheduleTimeZone.Normalize(timeZone);
        return this;
    }

    /// <summary>
    /// Configures global infrastructure knobs for the keyed rate limiter (parked-task cap,
    /// tracked-key cardinality bound, key length cap, deferral event emission).
    /// Per-task-type limits are declared on handlers via <see cref="RateLimitPolicy"/>.
    /// </summary>
    /// <param name="configure">Action to configure the rate limiter options.</param>
    /// <returns>The configuration instance for method chaining.</returns>
    /// <remarks>
    /// <code>
    /// opt.SetRateLimiterOptions(o =>
    /// {
    ///     o.MaxParkedTasks     = 5000;
    ///     o.MaxTrackedKeys     = 100_000;
    ///     o.MaxKeyLength       = 256;
    ///     o.EmitDeferralEvents = true;
    /// });
    /// </code>
    /// </remarks>
    public EverTaskServiceConfiguration SetRateLimiterOptions(Action<RateLimiterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(RateLimiterOptions);
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
        var cores = Environment.ProcessorCount;
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
        var cores = Environment.ProcessorCount;
        return Math.Max(4, cores * 2); // Min 4, ~16 su 8-core
    }
}

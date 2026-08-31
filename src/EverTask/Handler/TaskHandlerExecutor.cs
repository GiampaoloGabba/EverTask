using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace EverTask.Handler;

/// <summary>
/// Represents a task execution context with handler and metadata.
/// Supports both eager mode (handler instance present) and lazy mode (handler type stored for later resolution).
/// </summary>
/// <remarks>
/// <c>RateLimitPolicy</c> and <c>RateLimitKey</c> are stamped at dispatch time by the handler
/// wrapper and live only in memory (never persisted): recovered tasks re-extract them on
/// re-dispatch. Both are preserved by <see cref="ToLazy"/> and by <c>with</c> expressions.
/// </remarks>
public record TaskHandlerExecutor(
    IEverTask Task,
    object? Handler,
    string? HandlerTypeName,
    DateTimeOffset? ExecutionTime,
    RecurringTask? RecurringTask,
    Func<IEverTask, CancellationToken, Task>? HandlerCallback,
    Func<Guid, Exception?, string, ValueTask>? HandlerErrorCallback,
    Func<Guid, ValueTask>? HandlerStartedCallback,
    Func<Guid, ValueTask>? HandlerCompletedCallback,
    Guid PersistenceId,
    string? QueueName,
    string? TaskKey,
    AuditLevel AuditLevel,
    RateLimitPolicy? RateLimitPolicy = null,
    string? RateLimitKey = null,
    // Eager mode only: the EverTask-owned DI scope the carried handler instance was resolved from.
    // The worker disposes it right after execution so eager handlers are NOT pinned in the singleton
    // dispatcher's root container until shutdown. Memory-only; null for lazy executors and
    // dropped by ToLazy(). Never persisted.
    IAsyncDisposable? HandlerScope = null)
{
    // Occurrence metadata lives in INIT properties declared in the body, never as appended positional
    // parameters: appending would change the primary constructor and Deconstruct signatures, breaking every
    // consumer that constructs or deconstructs an executor. `with` expressions copy them for free.

    /// <summary>
    /// The recurring schedule row this executor is an occurrence of, or null for a schedule row / plain task.
    /// </summary>
    public Guid? ParentTaskId { get; init; }

    /// <summary>Occurrence metadata JSON carried to the persisted row (opaque to the worker).</summary>
    public string? RuntimeInfo { get; init; }

    /// <summary>1-based number of the run this delivery represents, when it is durably known.</summary>
    public int? RunNumber { get; init; }

    /// <summary>Version of the schedule definition this executor was built from (0 for unversioned rows).</summary>
    public int ScheduleVersion { get; init; }

    /// <summary>
    /// The nominal slot this delivery belongs to, when it differs from <see cref="ExecutionTime"/> (the
    /// rate-limit gate replaces the latter with its reserved slot). An occurrence carries the slot its ROW
    /// states, read from <see cref="RuntimeInfo"/> where the row becomes a task, so the answer never depends
    /// on which executor is delivering it — and when it was not stamped at all, <see cref="RowOccurrence"/>
    /// still reads it back from the same place.
    /// </summary>
    public DateTimeOffset? NominalSlotUtc { get; init; }

    /// <summary>
    /// True once the rate-limit gate replaced <see cref="ExecutionTime"/> with the slot it reserved for this
    /// task. Internal, and set only there: from that moment <see cref="ExecutionTime"/> answers "when does the
    /// scheduler fire this", not "which slot is this", and only <see cref="NominalSlotUtc"/> answers the latter.
    /// </summary>
    internal bool ExecutionTimeIsReservedSlot { get; init; }

    /// <summary>
    /// True when this delivery exists only to ask a schedule's grid AGAIN — an
    /// <see cref="INextOccurrenceProvider"/> that could not answer — and must not run the handler.
    /// </summary>
    /// <remarks>
    /// Internal and set in exactly one place, the provider re-park. What the schedule owes is decided from the
    /// ROW when the retry fires, which is the whole point: the question the provider left unanswered is which
    /// slot is due, so parking an ordinary delivery would run one nobody has decided about.
    /// </remarks>
    internal bool IsScheduleRetry { get; init; }

    /// <summary>
    /// On a <see cref="IsScheduleRetry"/> delivery: the slot the interrupted decision was about, since
    /// <see cref="ExecutionTime"/> now holds the instant the retry FIRES at.
    /// </summary>
    /// <remarks>
    /// It exists for the one host that has nowhere else to read it: with a storage the retry re-reads the row
    /// and the cursor there is the answer, but a storage-less series lives entirely on its delivery, and
    /// re-deciding from the retry instant instead of from the slot would silently skip everything in between.
    /// </remarks>
    internal DateTimeOffset? ScheduleRetryFromUtc { get; init; }

    /// <summary>
    /// True when an exclusion-search retry must resume the interrupted advance without counting its run
    /// again. Memory-only; a stored schedule reads the already-advanced counter from its row.
    /// </summary>
    internal bool ScheduleRunAlreadyRecorded { get; init; }

    /// <summary>
    /// True only for a newly persisted immediate dispatch whose row already carries the Queued status.
    /// </summary>
    internal bool IsNewImmediateDispatch { get; init; }

    private OccurrenceRuntimeInfo? _rowOccurrence;

    /// <summary>
    /// What the persisted ROW states about this delivery when it is an occurrence — its slot and its run of
    /// the series — or null when the delivery is not one.
    /// </summary>
    /// <remarks>
    /// The stamped <see cref="NominalSlotUtc"/> / <see cref="RunNumber"/> are the same two facts, read once
    /// where the row became a task; this is what answers when they were not stamped, so an occurrence handed
    /// straight to the scheduler after being materialized reports its slot and its run just like a recovered
    /// one. Only reached for an occurrence that arrived without them: every other delivery answers on the
    /// preceding null check, without parsing anything.
    /// </remarks>
    internal OccurrenceRuntimeInfo? RowOccurrence =>
        ParentTaskId != null ? _rowOccurrence ??= OccurrenceRuntimeInfo.TryParse(RuntimeInfo) : null;

    /// <summary>
    /// The slot this delivery stands for: the occurrence's own slot, the scheduled time of a delayed task, or
    /// null for a task dispatched to run immediately. Never the moving slot a rate-limit deferral parked it at
    /// — reporting that one would make a deferred task look as if it had been scheduled for it — and never the
    /// moment an overdue occurrence happened to be fired at either: an occurrence's slot comes from its own
    /// row, whether it was stamped on the executor or is still only in the row's metadata.
    /// </summary>
    internal DateTimeOffset? NominalSlotOfDelivery =>
        NominalSlotUtc ?? RowOccurrence?.SlotUtc ?? (ExecutionTimeIsReservedSlot ? null : ExecutionTime);

    /// <summary>
    /// True when this executor represents a DURABLE schedule row: it owns the definition and the cursor but
    /// never runs the handler — its due slots become child rows instead.
    /// </summary>
    public bool IsScheduleOnly => RecurringTask?.OccurrenceMode == OccurrenceMode.Durable;

    /// <summary>
    /// Indicates whether this executor is in lazy mode (handler not yet resolved).
    /// </summary>
    /// <remarks>
    /// Lazy mode is used for scheduled and recurring tasks to reduce memory footprint.
    /// Handler instances are resolved at execution time instead of dispatch time.
    /// </remarks>
    public bool IsLazy => Handler == null;

    // Performance optimization: cache Type.GetType lookups for lazy resolution.
    // The immediate path is lazy by default, so this lookup is on the hot path.
    private static readonly ConcurrentDictionary<string, Type> HandlerTypeLookupCache = new();

    // Performance optimization: compiled Handle invokers per (handler, task) type pair for
    // lazy-mode callbacks. A compiled delegate avoids MethodInfo.Invoke, which would both be
    // slower on the hot path and wrap synchronous handler exceptions in
    // TargetInvocationException (breaking retry-policy exception filtering).
    private static readonly ConcurrentDictionary<(Type HandlerType, Type TaskType),
        Func<object, IEverTask, CancellationToken, Task>?> HandleInvokerCache = new();

    // Performance optimization: closed IEverTaskHandler<TTask> generic per task type, for the
    // interface-binding fallback below
    private static readonly ConcurrentDictionary<Type, Type> HandlerInterfaceTypeCache = new();

    /// <summary>
    /// Resolves the handler instance from the service provider if in lazy mode,
    /// or returns the existing handler instance if in eager mode.
    /// </summary>
    /// <param name="serviceProvider">Service provider for DI resolution</param>
    /// <returns>Handler instance</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if handler type cannot be loaded or is not registered in DI
    /// </exception>
    public object GetOrResolveHandler(IServiceProvider serviceProvider)
    {
        // Eager mode: return existing handler instance
        if (Handler != null)
            return Handler;

        // Lazy mode: resolve from DI
        if (string.IsNullOrEmpty(HandlerTypeName))
            throw new InvalidOperationException(
                $"Cannot resolve handler for task {PersistenceId}: both Handler and HandlerTypeName are null");

        // Load type from assembly-qualified name (cached: Type.GetType parses the name on every call)
        if (!HandlerTypeLookupCache.TryGetValue(HandlerTypeName, out var handlerType))
        {
            handlerType = Type.GetType(HandlerTypeName);
            if (handlerType != null)
                HandlerTypeLookupCache.TryAdd(HandlerTypeName, handlerType);
        }

        if (handlerType == null)
            throw new InvalidOperationException(
                $"Handler type '{HandlerTypeName}' could not be loaded. Ensure assembly is referenced and type exists.");

        // Resolve handler from DI. Assembly scanning self-registers the concrete type, but a
        // manual registration may only bind IEverTaskHandler<TTask> → implementation: fall back
        // to the interface binding so immediate (lazy-by-default) dispatches keep working for
        // interface-only registrations.
        var handler = serviceProvider.GetService(handlerType);
        if (handler == null)
        {
            var interfaceType = HandlerInterfaceTypeCache.GetOrAdd(Task.GetType(),
                static taskType => typeof(IEverTaskHandler<>).MakeGenericType(taskType));
            handler = serviceProvider.GetService(interfaceType);
        }

        return handler
               ?? throw new InvalidOperationException(
                   $"Handler '{handlerType.Name}' is not registered in DI container. Call RegisterTasksFromAssembly() to register handlers.");
    }

    /// <summary>
    /// Creates a typed callback for the provided handler instance.
    /// Use this when the handler has already been resolved to avoid duplicate DI resolutions.
    /// </summary>
    /// <param name="handler">Pre-resolved handler instance</param>
    /// <returns>Tuple containing handler instance and typed callback</returns>
    public (object Handler, Func<IEverTask, CancellationToken, Task> Callback) CreateHandlerCallback(object handler)
    {
        // If we already have a callback (eager mode), return it
        if (HandlerCallback != null)
            return (handler, HandlerCallback);

        // Lazy mode: create typed callback via a compiled delegate (cached per type pair)
        var taskType = Task.GetType();
        var invoker = HandleInvokerCache.GetOrAdd(
            (handler.GetType(), taskType),
            static key =>
            {
                var handleMethod = key.HandlerType.GetMethod("Handle", [key.TaskType, typeof(CancellationToken)]);
                if (handleMethod == null)
                    return null;

                // (object handler, IEverTask task, CancellationToken ct) =>
                //     ((THandler)handler).Handle((TTask)task, ct)
                var handlerParam = Expression.Parameter(typeof(object), "handler");
                var taskParam    = Expression.Parameter(typeof(IEverTask), "task");
                var tokenParam   = Expression.Parameter(typeof(CancellationToken), "ct");

                var call = Expression.Call(
                    Expression.Convert(handlerParam, key.HandlerType),
                    handleMethod,
                    Expression.Convert(taskParam, key.TaskType),
                    tokenParam);

                return Expression
                       .Lambda<Func<object, IEverTask, CancellationToken, Task>>(
                           call, handlerParam, taskParam, tokenParam)
                       .Compile();
            });

        if (invoker == null)
            throw new InvalidOperationException(
                $"Handle method not found on handler {handler.GetType().Name} for task {taskType.Name}");

        return (handler, Callback);

        Task Callback(IEverTask t, CancellationToken ct) => invoker(handler, t, ct);
    }

    /// <summary>
    /// Converts this executor to lazy mode by removing the handler instance
    /// and storing only the handler type name.
    /// </summary>
    /// <returns>
    /// New lazy executor with null handler and populated HandlerTypeName,
    /// or a new instance with the same values if already lazy
    /// </returns>
    /// <remarks>
    /// The handler instance is NOT disposed here: its lifetime is owned by the DI scope or
    /// container that resolved it. Fresh handler instances are resolved and disposed at
    /// execution time inside the worker's per-task scope.
    /// </remarks>
    public TaskHandlerExecutor ToLazy() =>
        // ALWAYS a new instance, even when already lazy: the channel consumer needs a NEW reference so
        // Parallel.ForEachAsync can process recurring tasks. `with` copies every member — the positional
        // ones AND the init-only occurrence metadata — so a new member can never be forgotten here;
        // only the handler instance, its callbacks and its owned scope are dropped. The handler type name
        // is reused when stamped at dispatch, else derived from the instance being dropped.
        this with
        {
            Handler = null,
            HandlerTypeName = HandlerTypeName ?? (Handler != null
                ? TypeNameCache.GetAssemblyQualifiedName(Handler.GetType())
                : null),
            HandlerCallback = null,
            HandlerErrorCallback = null,
            HandlerStartedCallback = null,
            HandlerCompletedCallback = null,
            HandlerScope = null,
            // Born-Queued applies only to the first handoff; every lazy copy is a later delivery.
            IsNewImmediateDispatch = false
        };
};

public static class TaskHandlerExecutorExtensions
{
    /// <summary>
    /// Maps an executor to the row that persists it, stamped from the real clock.
    /// </summary>
    /// <remarks>
    /// The zero-extra-argument shape is preserved exactly: an assembly compiled against the previous release
    /// calls THIS signature, and turning it into an optional parameter would have removed it.
    /// </remarks>
    public static QueuedTask ToQueuedTask(this TaskHandlerExecutor executor) => executor.ToQueuedTask(null);

    /// <param name="createdAtUtc">
    /// The scheduling clock's "now", stamped as <see cref="QueuedTask.CreatedAtUtc"/>. The recovery cutoff
    /// compares that column against the same clock, so a host running on an injected one must not stamp its
    /// rows from the wall clock or recovery would never see them. Null falls back to the real clock.
    /// </param>
    public static QueuedTask ToQueuedTask(this TaskHandlerExecutor executor, DateTimeOffset? createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(executor.Task);

        var request = EverTaskJson.Serialize(executor.Task);

        // Use the shared assembly-qualified-name cache to avoid repeated string generation
        var requestType = TypeNameCache.GetAssemblyQualifiedName(executor.Task.GetType());

        // Get handler type from Handler instance (eager mode) or HandlerTypeName (lazy mode)
        string handlerType;
        if (executor.Handler != null)
        {
            // Eager mode: extract from handler instance
            handlerType = TypeNameCache.GetAssemblyQualifiedName(executor.Handler.GetType());
        }
        else if (!string.IsNullOrEmpty(executor.HandlerTypeName))
        {
            // Lazy mode: use stored type name
            handlerType = executor.HandlerTypeName;
        }
        else
        {
            throw new InvalidOperationException(
                $"Cannot serialize executor: both Handler and HandlerTypeName are null for task {executor.PersistenceId}");
        }

        var            isRecurring      = false;
        string?         scheduleTask     = null;
        DateTimeOffset? nextRun          = null;
        string?         scheduleTaskInfo = null;
        int?            maxRuns          = null;
        DateTimeOffset? runUntil         = null;

        if (executor.RecurringTask != null)
        {
            scheduleTask = EverTaskJson.Serialize(executor.RecurringTask);
            isRecurring  = true;

            // The first NextRunUtc is DECIDED BY THE DISPATCHER and arrives here as ExecutionTime: every
            // persisted dispatch goes through the schedule evaluator first, so this mapping never re-derives
            // the grid. The fallback below only serves direct callers of this extension that skipped the
            // dispatcher (it is unreachable from the library's own paths).
            if (executor.ExecutionTime.HasValue)
            {
                nextRun = executor.ExecutionTime;
            }
            else
            {
                var referenceTime = DateTimeOffset.UtcNow;
                var result =
                    executor.RecurringTask.CalculateNextValidRun(referenceTime, 0, referenceTime: referenceTime);
                nextRun = result.NextRun;
            }

            // Computed inline, never cached: every persisted dispatch builds a fresh RecurringTask, so a
            // cache keyed by reference identity never hits and only retains one entry per dispatch for ever.
            scheduleTaskInfo = executor.RecurringTask.ToString() ?? "Recurring Task";

            maxRuns  = executor.RecurringTask.MaxRuns;
            runUntil = executor.RecurringTask.RunUntil;
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(handlerType);

        return new QueuedTask
        {
            Id                    = executor.PersistenceId,
            Type                  = requestType,
            Request               = request,
            Handler               = handlerType,
            Status                = executor.IsNewImmediateDispatch
                                        ? QueuedTaskStatus.Queued
                                        : QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc          = createdAtUtc ?? DateTimeOffset.UtcNow,
            ScheduledExecutionUtc = executor.ExecutionTime,
            IsRecurring           = isRecurring,
            RecurringTask         = scheduleTask,
            RecurringInfo         = scheduleTaskInfo,
            MaxRuns               = maxRuns,
            RunUntil              = runUntil,
            NextRunUtc            = nextRun,
            CurrentRunCount       = 0,
            QueueName             = executor.QueueName,
            TaskKey               = executor.TaskKey,
            AuditLevel            = (int)executor.AuditLevel,
            ParentTaskId          = executor.ParentTaskId,
            RuntimeInfo           = executor.RuntimeInfo,
            ScheduleVersion       = executor.ScheduleVersion
        };
    }
}

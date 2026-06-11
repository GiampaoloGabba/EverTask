using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace EverTask.Handler;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the NotificationHandlerExecutor.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/NotificationHandlerExecutor.cs

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
    string? RateLimitKey = null)
{

    /// <summary>
    /// Indicates whether this executor is in lazy mode (handler not yet resolved).
    /// </summary>
    /// <remarks>
    /// Lazy mode is used for scheduled and recurring tasks to reduce memory footprint.
    /// Handler instances are resolved at execution time instead of dispatch time.
    /// </remarks>
    public bool IsLazy => Handler == null;

    /// <summary>
    /// Resolves the handler instance from the service provider if in lazy mode,
    /// or returns the existing handler instance if in eager mode.
    /// </summary>
    /// <param name="serviceProvider">Service provider for DI resolution</param>
    /// <returns>Handler instance</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if handler type cannot be loaded or is not registered in DI
    /// </exception>
    // Performance optimization: cache Type.GetType lookups for lazy resolution.
    // The immediate path is lazy by default, so this lookup is on the hot path.
    private static readonly ConcurrentDictionary<string, Type> HandlerTypeLookupCache = new();

    // Performance optimization: compiled Handle invokers per (handler, task) type pair for
    // lazy-mode callbacks. A compiled delegate avoids MethodInfo.Invoke, which would both be
    // slower on the hot path and wrap synchronous handler exceptions in
    // TargetInvocationException (breaking retry-policy exception filtering).
    private static readonly ConcurrentDictionary<(Type HandlerType, Type TaskType),
        Func<object, IEverTask, CancellationToken, Task>?> HandleInvokerCache = new();

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

        // Resolve handler from DI
        var handler = serviceProvider.GetService(handlerType)
                      ?? throw new InvalidOperationException(
                          $"Handler '{handlerType.Name}' is not registered in DI container. Call RegisterTasksFromAssembly() to register handlers.");

        return handler;
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
    public TaskHandlerExecutor ToLazy()
    {
        // ALWAYS create a new instance to ensure Parallel.ForEachAsync can process recurring tasks
        // Even if already lazy, we need a NEW reference for the channel consumer to pick up
        if (IsLazy)
        {
            // Create a NEW instance with the same values
            return new TaskHandlerExecutor(
                Task,
                Handler: null,
                HandlerTypeName,
                ExecutionTime,
                RecurringTask,
                HandlerCallback: null,
                HandlerErrorCallback: null,
                HandlerStartedCallback: null,
                HandlerCompletedCallback: null,
                PersistenceId,
                QueueName,
                TaskKey,
                AuditLevel,
                RateLimitPolicy,
                RateLimitKey
            );
        }

        // Reuse the handler type name stamped at dispatch time when available,
        // falling back to the shared type-name cache (AQN dedup)
        var handlerTypeName = HandlerTypeName ?? TypeNameCache.GetAssemblyQualifiedName(Handler!.GetType());

        // Create lazy executor with handler and callbacks set to null
        return new TaskHandlerExecutor(
            Task,
            Handler: null,
            HandlerTypeName: handlerTypeName,
            ExecutionTime,
            RecurringTask,
            HandlerCallback: null,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId,
            QueueName,
            TaskKey,
            AuditLevel,
            RateLimitPolicy,
            RateLimitKey
        );
    }
};

public static class TaskHandlerExecutorExtensions
{
    // Performance optimization: Cache type metadata strings to avoid repeated generation
    private static readonly ConcurrentDictionary<RecurringTask, string> RecurringTaskToStringCache = new();

    public static QueuedTask ToQueuedTask(this TaskHandlerExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor.Task);

        var request = JsonConvert.SerializeObject(executor.Task);

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

        bool            isRecurring      = false;
        string?         scheduleTask     = null;
        DateTimeOffset? nextRun          = null;
        string?         scheduleTaskInfo = null;
        int?            maxRuns          = null;
        DateTimeOffset? runUntil         = null;

        if (executor.RecurringTask != null)
        {
            scheduleTask = JsonConvert.SerializeObject(executor.RecurringTask);
            isRecurring  = true;

            // For a newly dispatched recurring task, NextRunUtc should be the time of the first execution
            // (same as ScheduledExecutionUtc). If ExecutionTime is null (e.g., immediate execution),
            // calculate the first occurrence from UtcNow.
            if (executor.ExecutionTime.HasValue)
            {
                nextRun = executor.ExecutionTime;
            }
            else
            {
                // Calculate first occurrence for immediate execution
                var referenceTime = DateTimeOffset.UtcNow;
                var result =
                    executor.RecurringTask.CalculateNextValidRun(referenceTime, 0, referenceTime: referenceTime);
                nextRun = result.NextRun;
            }

            // Cache RecurringTask.ToString() result to avoid repeated string generation
            scheduleTaskInfo = RecurringTaskToStringCache.GetOrAdd(
                executor.RecurringTask,
                rt => rt.ToString() ?? "Recurring Task");

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
            Status                = QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc          = DateTimeOffset.UtcNow,
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
            AuditLevel            = (int)executor.AuditLevel
        };
    }
}

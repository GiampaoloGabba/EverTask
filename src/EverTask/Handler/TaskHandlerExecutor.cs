using System.Collections.Concurrent;

namespace EverTask.Handler;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the NotificationHandlerExecutor.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/NotificationHandlerExecutor.cs

/// <summary>
/// Represents a task execution context with handler and metadata.
/// Supports both eager mode (handler instance present) and lazy mode (handler type stored for later resolution).
/// </summary>
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
    string? TaskKey)
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
    public object GetOrResolveHandler(IServiceProvider serviceProvider)
    {
        // Eager mode: return existing handler instance
        if (Handler != null)
            return Handler;

        // Lazy mode: resolve from DI
        if (string.IsNullOrEmpty(HandlerTypeName))
            throw new InvalidOperationException(
                $"Cannot resolve handler for task {PersistenceId}: both Handler and HandlerTypeName are null");

        // Load type from assembly-qualified name
        var handlerType = Type.GetType(HandlerTypeName);
        if (handlerType == null)
            throw new InvalidOperationException(
                $"Handler type '{HandlerTypeName}' could not be loaded. Ensure assembly is referenced and type exists.");

        // Resolve handler from DI
        var handler = serviceProvider.GetService(handlerType);
        if (handler == null)
            throw new InvalidOperationException(
                $"Handler '{handlerType.Name}' is not registered in DI container. Call RegisterTasksFromAssembly() to register handlers.");

        return handler;
    }

    /// <summary>
    /// Resolves the handler and creates a typed callback for invocation.
    /// This method is more performant than reflection-based invocation.
    /// </summary>
    /// <param name="serviceProvider">Service provider for DI resolution</param>
    /// <returns>Tuple containing handler instance and typed callback</returns>
    public (object Handler, Func<IEverTask, CancellationToken, Task> Callback)
        GetOrResolveHandlerWithCallback(IServiceProvider serviceProvider)
    {
        var handler = GetOrResolveHandler(serviceProvider);

        // If we already have a callback (eager mode), return it
        if (HandlerCallback != null)
            return (handler, HandlerCallback);

        // Lazy mode: create typed callback using reflection
        var taskType = Task.GetType();
        var handleMethod = handler.GetType().GetMethod("Handle", new[] { taskType, typeof(CancellationToken) });

        if (handleMethod == null)
            throw new InvalidOperationException(
                $"Handle method not found on handler {handler.GetType().Name} for task {taskType.Name}");

        Task Callback(IEverTask t, CancellationToken ct) => (Task)handleMethod.Invoke(handler, [t, ct])!;

        return (handler, Callback);
    }

    /// <summary>
    /// Converts this executor to lazy mode by removing the handler instance
    /// and storing only the handler type name.
    /// </summary>
    /// <returns>
    /// New lazy executor with null handler and populated HandlerTypeName,
    /// or self if already lazy
    /// </returns>
    /// <remarks>
    /// The original handler instance at dispatch time is NOT disposed. It will be collected by GC.
    /// This is intentional: the handler has not executed yet, so calling DisposeAsyncCore() would
    /// be inappropriate (user's dispose logic expects handler to have been executed).
    /// Fresh handler instances are resolved and disposed at execution time.
    /// </remarks>
    public TaskHandlerExecutor ToLazy()
    {
        // Already lazy, return self
        if (IsLazy)
            return this;

        // Extract handler type name
        var handlerTypeName = Handler!.GetType().AssemblyQualifiedName
            ?? throw new InvalidOperationException($"Handler type {Handler.GetType().Name} has no AssemblyQualifiedName");

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
            TaskKey
        );
    }
};

public static class TaskHandlerExecutorExtensions
{
    // Performance optimization: Cache type metadata strings to avoid repeated generation
    private static readonly ConcurrentDictionary<Type, string> AssemblyQualifiedNameCache = new();
    private static readonly ConcurrentDictionary<RecurringTask, string> RecurringTaskToStringCache = new();

    public static QueuedTask ToQueuedTask(this TaskHandlerExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor.Task);

        var request = JsonConvert.SerializeObject(executor.Task);

        // Use cached AssemblyQualifiedName to avoid repeated string generation
        var requestType = AssemblyQualifiedNameCache.GetOrAdd(
            executor.Task.GetType(),
            type => type.AssemblyQualifiedName ?? throw new InvalidOperationException($"Type {type} has no AssemblyQualifiedName"));

        // Get handler type from Handler instance (eager mode) or HandlerTypeName (lazy mode)
        string handlerType;
        if (executor.Handler != null)
        {
            // Eager mode: extract from handler instance
            handlerType = AssemblyQualifiedNameCache.GetOrAdd(
                executor.Handler.GetType(),
                type => type.AssemblyQualifiedName ?? throw new InvalidOperationException($"Type {type} has no AssemblyQualifiedName"));
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
            isRecurring = true;

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
                var result = executor.RecurringTask.CalculateNextValidRun(referenceTime, 0, referenceTime: referenceTime);
                nextRun = result.NextRun;
            }

            // Cache RecurringTask.ToString() result to avoid repeated string generation
            scheduleTaskInfo = RecurringTaskToStringCache.GetOrAdd(
                executor.RecurringTask,
                rt => rt.ToString() ?? "Recurring Task");

            maxRuns = executor.RecurringTask.MaxRuns;
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
            TaskKey               = executor.TaskKey
        };
    }
}

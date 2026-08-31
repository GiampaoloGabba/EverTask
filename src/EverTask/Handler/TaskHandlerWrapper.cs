using System.Collections.Concurrent;
using EverTask.Configuration;
using EverTask.Dispatcher;

namespace EverTask.Handler;

internal abstract class TaskHandlerWrapper
{
    public abstract ValueTask<TaskHandlerExecutor> Handle(IEverTask task, DateTimeOffset? executionTime,
                                                          RecurringTask? recurring, IServiceProvider serviceFactory,
                                                          AuditLevel auditLevel, Guid? existingTaskId = null,
                                                          string? taskKey = null, bool useLazyExecutor = false,
                                                          DispatchRowMetadata rowMetadata = default,
                                                          bool isNewImmediateDispatch = false);
}

internal sealed class TaskHandlerWrapperImp<TTask> : TaskHandlerWrapper where TTask : IEverTask
{
    // Rate-limit policy cached per concrete handler type, first-wins (same pattern as
    // WorkerExecutor.HandlerOptionsInternalCache). Policy validation happens in the
    // RateLimitPolicy constructor at first dispatch per type.
    private static readonly ConcurrentDictionary<Type, RateLimitPolicy?> RateLimitPolicyCache = new();

    // Once-per-handler-type guard for the recurring-interval sanity warning
    private static readonly ConcurrentDictionary<Type, byte> RecurringSanityWarned = new();

    public override async ValueTask<TaskHandlerExecutor> Handle(IEverTask task, DateTimeOffset? executionTime,
                                                                RecurringTask? recurring,
                                                                IServiceProvider serviceFactory,
                                                                AuditLevel auditLevel, Guid? existingTaskId = null,
                                                                string? taskKey = null, bool useLazyExecutor = false,
                                                                DispatchRowMetadata rowMetadata = default,
                                                                bool isNewImmediateDispatch = false)
    {
        var guidGenerator = serviceFactory.GetRequiredService<IGuidGenerator>();

        executionTime = executionTime?.ToUniversalTime();

        if (useLazyExecutor)
        {
            // Lazy executor: the handler is resolved only to extract per-type metadata (queue name,
            // handler type name, rate-limit policy) plus the per-dispatch rate-limit key, and is
            // released with this short-lived scope. Resolving it from the root provider would pin
            // disposable transient handlers in the root container's disposables list until shutdown.
            // The executing instance is resolved fresh by the worker in its own per-task scope.
            var scopeFactory = serviceFactory.GetRequiredService<IServiceScopeFactory>();
            await using var scope = scopeFactory.CreateAsyncScope();

            var scopedHandler = scope.ServiceProvider.GetService<IEverTaskHandler<TTask>>();
            ArgumentNullException.ThrowIfNull(scopedHandler);

            var (policy, rateLimitKey) = ExtractRateLimit(scopedHandler, (TTask)task, recurring, serviceFactory);

            return new TaskHandlerExecutor(
                task,
                Handler: null,
                TypeNameCache.GetAssemblyQualifiedName(scopedHandler.GetType()),
                executionTime,
                recurring,
                HandlerCallback: null,
                HandlerErrorCallback: null,
                HandlerStartedCallback: null,
                HandlerCompletedCallback: null,
                existingTaskId ?? guidGenerator.NewDatabaseFriendly(),
                // A stored queue wins over the handler attribute: the row was routed there once and the
                // recovery loop still groups it by that value.
                rowMetadata.QueueName ?? ResolveQueueName(scopedHandler, recurring),
                taskKey,
                auditLevel,
                policy,
                rateLimitKey
            )
            {
                ParentTaskId           = rowMetadata.ParentTaskId,
                RuntimeInfo            = rowMetadata.RuntimeInfo,
                ScheduleVersion        = rowMetadata.ScheduleVersion,
                RunNumber              = rowMetadata.RunNumber,
                NominalSlotUtc         = rowMetadata.NominalSlotUtc,
                IsNewImmediateDispatch = isNewImmediateDispatch
            };
        }

        // Eager executor: the handler instance is carried to execution time inside an EverTask-OWNED
        // scope (NOT the singleton dispatcher's root provider). Resolving from the root would pin the
        // IAsyncDisposable transient handler in the root container's disposables list until shutdown
        // and have it disposed twice (worker + root). The worker disposes this scope right after
        // execution, releasing the handler deterministically; recurring continuations
        // go lazy (WorkerExecutor.QueueNextOccourrence) so the carried scope is always single-use.
        var handlerScopeFactory = serviceFactory.GetRequiredService<IServiceScopeFactory>();
        var handlerScope        = handlerScopeFactory.CreateAsyncScope();

        try
        {
            var handlerService = handlerScope.ServiceProvider.GetService<IEverTaskHandler<TTask>>();
            ArgumentNullException.ThrowIfNull(handlerService);

            // Resolve via the concrete type (registered transient by HandlerRegistrar), like the lazy
            // path does, so a manual singleton registration of IEverTaskHandler<TTask> cannot hand the
            // SAME mutable instance to concurrent dispatches: the worker sets per-execution state (log
            // capture) on the carried handler, so a shared instance corrupts concurrent executions.
            if (handlerScope.ServiceProvider.GetService(handlerService.GetType()) is IEverTaskHandler<TTask> concreteHandler)
            {
                handlerService = concreteHandler;
            }

            var (eagerPolicy, eagerKey) = ExtractRateLimit(handlerService, (TTask)task, recurring, serviceFactory);

            return new TaskHandlerExecutor(
                task,
                handlerService,
                TypeNameCache.GetAssemblyQualifiedName(handlerService.GetType()),
                executionTime,
                recurring,
                (theTask, theToken) => handlerService.Handle((TTask)theTask, theToken),
                (persistenceId, exception, message) => handlerService.OnError(persistenceId, exception, message),
                persistenceId => handlerService.OnStarted(persistenceId),
                persistenceId => handlerService.OnCompleted(persistenceId),
                existingTaskId ?? guidGenerator.NewDatabaseFriendly(),
                rowMetadata.QueueName ?? ResolveQueueName(handlerService, recurring),
                taskKey,
                auditLevel,
                eagerPolicy,
                eagerKey,
                handlerScope
            )
            {
                ParentTaskId           = rowMetadata.ParentTaskId,
                RuntimeInfo            = rowMetadata.RuntimeInfo,
                ScheduleVersion        = rowMetadata.ScheduleVersion,
                RunNumber              = rowMetadata.RunNumber,
                NominalSlotUtc         = rowMetadata.NominalSlotUtc,
                IsNewImmediateDispatch = isNewImmediateDispatch
            };
        }
        catch
        {
            // Never leak the scope if executor construction fails before it is handed off.
            await handlerScope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Extracts the rate-limit policy (cached per handler type, first-wins) and the per-dispatch
    /// rate-limit key. A failing key selector is fail-safe: the dispatch proceeds ungated
    /// (consistent with the never-lose-a-task contract) with a warning log.
    /// </summary>
    private static (RateLimitPolicy? Policy, string? Key) ExtractRateLimit(
        IEverTaskHandler<TTask> handler, TTask task, RecurringTask? recurring, IServiceProvider serviceFactory)
    {
        // A DURABLE schedule row never runs the handler: it must not carry the handler's policy (it would
        // spend the key's budget on a row that executes nothing, and starve the occurrences it produces), and
        // the "recurrence faster than the limiter" warning does not apply to it either — that one is about the
        // occurrences, which are gated one by one.
        if (recurring is { OccurrenceMode: OccurrenceMode.Durable })
            return (null, null);

        var handlerType = handler.GetType();

        var policy = RateLimitPolicyCache.GetOrAdd(
            handlerType,
            static (_, h) => (h as IEverTaskHandlerOptions)?.RateLimitPolicy,
            handler);

        if (policy == null)
            return (null, null);

        // Sanity check at first rate-limited recurring dispatch per type: a recurrence faster
        // than the refill rate piles occurrences up behind the limiter
        if (recurring != null && RecurringSanityWarned.TryAdd(handlerType, 0))
        {
            var emissionInterval = TimeSpan.FromTicks(policy.Period.Ticks / policy.Permits);
            var minInterval      = recurring.GetMinimumInterval();
            if (minInterval < emissionInterval)
            {
                serviceFactory.GetService<IEverTaskLogger<TaskHandlerWrapperImp<TTask>>>()
                              ?.RecurringFasterThanRateLimit(typeof(TTask).Name, minInterval, emissionInterval);
            }
        }

        try
        {
            return (policy, handler.GetRateLimitKey(task));
        }
        catch (Exception ex)
        {
            // Fail-safe: a broken key selector must not lose the task; it executes ungated
            serviceFactory.GetService<IEverTaskLogger<TaskHandlerWrapperImp<TTask>>>()
                          ?.RateLimitKeySelectorFailed(ex, typeof(TTask).Name);
            return (policy, null);
        }
    }

    /// <summary>
    /// Extracts the queue name from handler options, normalized so it is always set in storage:
    /// null falls back to "recurring" for recurring tasks, "default" otherwise.
    /// </summary>
    private static string ResolveQueueName(IEverTaskHandler<TTask> handler, RecurringTask? recurring)
    {
        string? queueName = null;
        if (handler is IEverTaskHandlerOptions handlerOptions)
        {
            queueName = handlerOptions.QueueName;
        }

        return string.IsNullOrEmpty(queueName)
                   ? recurring != null ? QueueNames.Recurring : QueueNames.Default
                   : queueName;
    }
}

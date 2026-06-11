using EverTask.Abstractions;
using EverTask.Configuration;

namespace EverTask.Handler;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the NotificationHandlerWrapper.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Wrappers/NotificationHandlerWrapper.cs

internal abstract class TaskHandlerWrapper
{
    public abstract ValueTask<TaskHandlerExecutor> Handle(IEverTask task, DateTimeOffset? executionTime,
                                                          RecurringTask? recurring, IServiceProvider serviceFactory,
                                                          AuditLevel auditLevel, Guid? existingTaskId = null,
                                                          string? taskKey = null, bool useLazyExecutor = false);
}

internal sealed class TaskHandlerWrapperImp<TTask> : TaskHandlerWrapper where TTask : IEverTask
{
    public override async ValueTask<TaskHandlerExecutor> Handle(IEverTask task, DateTimeOffset? executionTime,
                                                                RecurringTask? recurring,
                                                                IServiceProvider serviceFactory,
                                                                AuditLevel auditLevel, Guid? existingTaskId = null,
                                                                string? taskKey = null, bool useLazyExecutor = false)
    {
        var guidGenerator = serviceFactory.GetRequiredService<IGuidGenerator>();

        executionTime = executionTime?.ToUniversalTime();

        if (useLazyExecutor)
        {
            // Lazy executor: the handler is resolved only to extract per-type metadata (queue name,
            // handler type name) and is released with this short-lived scope. Resolving it from the
            // root provider would pin disposable transient handlers in the root container's
            // disposables list until shutdown (MEM-2). The executing instance is resolved fresh by
            // the worker in its own per-task scope.
            var scopeFactory = serviceFactory.GetRequiredService<IServiceScopeFactory>();
            await using var scope = scopeFactory.CreateAsyncScope();

            var scopedHandler = scope.ServiceProvider.GetService<IEverTaskHandler<TTask>>();
            ArgumentNullException.ThrowIfNull(scopedHandler);

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
                ResolveQueueName(scopedHandler, recurring),
                taskKey,
                auditLevel
            );
        }

        // Eager executor: the handler instance is carried to execution time.
        var handlerService = serviceFactory.GetService<IEverTaskHandler<TTask>>();

        ArgumentNullException.ThrowIfNull(handlerService);

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
            ResolveQueueName(handlerService, recurring),
            taskKey,
            auditLevel
        );
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

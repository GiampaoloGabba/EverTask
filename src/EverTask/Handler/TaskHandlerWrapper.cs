using EverTask.Abstractions;
using EverTask.Configuration;

namespace EverTask.Handler;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the NotificationHandlerWrapper.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Wrappers/NotificationHandlerWrapper.cs

internal abstract class TaskHandlerWrapper
{
    public abstract TaskHandlerExecutor Handle(IEverTask task, DateTimeOffset? executionTime, RecurringTask? recurring,
                                               IServiceProvider serviceFactory, AuditLevel auditLevel, Guid? existingTaskId = null, string? taskKey = null);
}

internal sealed class TaskHandlerWrapperImp<TTask> : TaskHandlerWrapper where TTask : IEverTask
{
    public override TaskHandlerExecutor Handle(IEverTask task, DateTimeOffset? executionTime, RecurringTask? recurring,
                                               IServiceProvider serviceFactory, AuditLevel auditLevel, Guid? existingTaskId = null, string? taskKey = null)
    {
        var handlerService = serviceFactory.GetService<IEverTaskHandler<TTask>>();
        var guidGenerator = serviceFactory.GetRequiredService<IGuidGenerator>();

        executionTime = executionTime?.ToUniversalTime();

        ArgumentNullException.ThrowIfNull(handlerService);

        // Extract QueueName from handler options
        string? queueName = null;
        if (handlerService is IEverTaskHandlerOptions handlerOptions)
        {
            queueName = handlerOptions.QueueName;
        }

        // Normalize QueueName: if null and recurring, use "recurring"; otherwise use "default"
        // This ensures QueueName is always set in storage for clarity
        if (string.IsNullOrEmpty(queueName))
        {
            queueName = recurring != null ? QueueNames.Recurring : QueueNames.Default;
        }

        // Extract handler type name for lazy resolution support
        var handlerTypeName = handlerService.GetType().AssemblyQualifiedName
            ?? throw new InvalidOperationException($"Handler type {handlerService.GetType().Name} has no AssemblyQualifiedName");

        return new TaskHandlerExecutor(
            task,
            handlerService,
            handlerTypeName,
            executionTime,
            recurring,
            (theTask, theToken) => handlerService.Handle((TTask)theTask, theToken),
            (persistenceId, exception, message) => handlerService.OnError(persistenceId, exception, message),
            persistenceId => handlerService.OnStarted(persistenceId),
            persistenceId => handlerService.OnCompleted(persistenceId),
            existingTaskId ?? guidGenerator.NewDatabaseFriendly(),
            queueName,
            taskKey,
            auditLevel
        );
    }
}

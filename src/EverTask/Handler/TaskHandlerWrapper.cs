namespace EverTask.Handler;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the NotificationHandlerWrapper.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Wrappers/NotificationHandlerWrapper.cs

internal abstract class TaskHandlerWrapper
{
    public abstract TaskHandlerExecutor Handle(IEverTask task, DateTimeOffset? executionTime, RecurringTask? recurring,
                                               IServiceProvider serviceFactory, Guid? existingTaskId = null);
}

internal sealed class TaskHandlerWrapperImp<TTask> : TaskHandlerWrapper where TTask : IEverTask
{
    public override TaskHandlerExecutor Handle(IEverTask task, DateTimeOffset? executionTime, RecurringTask? recurring,
                                               IServiceProvider serviceFactory, Guid? existingTaskId = null)
    {
        var handlerService = serviceFactory.GetService<IEverTaskHandler<TTask>>();

        executionTime = executionTime?.ToUniversalTime();

        ArgumentNullException.ThrowIfNull(handlerService);

        return new TaskHandlerExecutor(
            task,
            handlerService,
            executionTime,
            recurring,
            (theTask, theToken) => handlerService.Handle((TTask)theTask, theToken),
            (persistenceId, exception, message) => handlerService.OnError(persistenceId, exception, message),
            persistenceId => handlerService.OnStarted(persistenceId),
            persistenceId => handlerService.OnCompleted(persistenceId),
            existingTaskId ?? Guid.NewGuid()
        );
    }
}

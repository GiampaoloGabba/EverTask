﻿namespace EverTask.Handler;

internal abstract class TaskHandlerWrapper
{
    public abstract TaskHandlerExecutor Handle(IEverTask task, IServiceProvider serviceFactory, Guid? existingTaskId = null);
}

internal class TaskHandlerWrapperImp<TTask> : TaskHandlerWrapper where TTask : IEverTask
{
    public override TaskHandlerExecutor Handle(IEverTask task, IServiceProvider serviceFactory, Guid? existingTaskId = null)
    {
        var handlerService = serviceFactory.GetService<IEverTaskHandler<TTask>>();

        ArgumentNullException.ThrowIfNull(handlerService);

        return new TaskHandlerExecutor(
            task,
            handlerService,
            (theTask, theToken) => handlerService.Handle((TTask)theTask, theToken),
            (exception, message) => handlerService.OnError(exception, message),
            () => handlerService.Completed(),
            existingTaskId ?? Guid.NewGuid()
        );
    }
}

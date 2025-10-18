using System.Net;

namespace EverTask.Tests;

// Test tasks for error handling scenarios

public record TestTaskRequestError() : IEverTask;

public record TestTaskRequestNoSerializable(IPAddress notSerializable) : IEverTask;

public record ThrowStorageError() : IEverTask;

public class TestTaskRequestErrorHandler : EverTaskHandler<TestTaskRequestError>
{
    public override Task Handle(TestTaskRequestError backgroundTask, CancellationToken cancellationToken)
    {
        throw new Exception("Not executable handler");
    }
}

public class TestTaskHandlertNoSerializable : EverTaskHandler<TestTaskRequestNoSerializable>
{
    public override Task Handle(TestTaskRequestNoSerializable backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class ThrowStorageErrorHanlder : EverTaskHandler<ThrowStorageError>
{
    public override Task Handle(ThrowStorageError backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

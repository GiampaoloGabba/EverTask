namespace EverTask.Tests;

// Basic test tasks and handlers

public record TestTaskRequest(string Name) : IEverTask;

public record TestTaskRequest2() : IEverTask;

public record TestTaskRequest3() : IEverTask;

public record TestTaskRequestNoHandler : IEverTask;

public class TestTaskHanlder : EverTaskHandler<TestTaskRequest>
{
    public override Task Handle(TestTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class TestTaskHanlderDuplicate : EverTaskHandler<TestTaskRequest>
{
    public override Task Handle(TestTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class TestTaskHanlder2 : EverTaskHandler<TestTaskRequest2>
{
    public override Task Handle(TestTaskRequest2 backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class TestTaskHanlder3 : EverTaskHandler<TestTaskRequest3>
{
    public override Task Handle(TestTaskRequest3 backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

record InternalTestTaskRequest : IEverTask;

class TestInternalTaskHanlder : EverTaskHandler<InternalTestTaskRequest>
{
    public override Task Handle(InternalTestTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

namespace EverTask.Tests;

public record TestTaskRequest(string Name) : IEverTask;

public record TestTaskRequest2() : IEverTask;

public record TestTaskRequest3() : IEverTask;

public record TestTaskRequestNoHandler : IEverTask;

public class TestTaskConcurrent1() : IEverTask
{
    public static int      Counter   { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime   { get; set; }
};

public class TestTaskConcurrent2() : IEverTask
{
    public static int      Counter   { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime   { get; set; }
};

public record TestTaskRequestNoSerializable(IPAddress notSerializable) : IEverTask;

public record ThrowStorageError() : IEverTask;

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

public class TestTaskConcurrent1Handler : EverTaskHandler<TestTaskConcurrent1>
{
    public override async Task Handle(TestTaskConcurrent1 backgroundTask, CancellationToken cancellationToken)
    {
        TestTaskConcurrent1.StartTime = DateTime.Now;
        await Task.Delay(500, cancellationToken);
        TestTaskConcurrent1.Counter = 1;
        TestTaskConcurrent1.EndTime = DateTime.Now;
    }
}

public class TestTaskConcurrent2Handler : EverTaskHandler<TestTaskConcurrent2>
{
    public override async Task Handle(TestTaskConcurrent2 backgroundTask, CancellationToken cancellationToken)
    {
        TestTaskConcurrent2.StartTime = DateTime.Now;
        await Task.Delay(500, cancellationToken);
        TestTaskConcurrent2.Counter = 1;
        TestTaskConcurrent2.EndTime = DateTime.Now;
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

record InternalTestTaskRequest : IEverTask;

class TestInternalTaskHanlder : EverTaskHandler<InternalTestTaskRequest>
{
    public override Task Handle(InternalTestTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

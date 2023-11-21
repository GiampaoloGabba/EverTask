using EverTask.Resilience;

namespace EverTask.Tests;

public record TestTaskRequest(string Name) : IEverTask;

public record TestTaskRequest2() : IEverTask;

public record TestTaskRequest3() : IEverTask;

public record TestTaskRequestNoHandler : IEverTask;

public record TestTaskRequestError() : IEverTask;

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

public class TestTaskDelayed1() : IEverTask
{
    public static int Counter { get; set; } = 0;
};

public class TestTaskDelayed2() : IEverTask
{
    public static int Counter { get; set; } = 0;
};

public class TestTaskCpubound() : IEverTask
{
    public static int Counter { get; set; } = 0;
};

public class TestTaskWithRetryPolicy() : IEverTask
{
    public static int Counter { get; set; } = 0;
};

public class TestTaskWithCustomRetryPolicy() : IEverTask
{
    public static int Counter { get; set; } = 0;
};

public class TestTaskWithCustomTimeout() : IEverTask
{
    public static int Counter { get; set; } = 0;
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
public class TestTaskRequestErrorHandler : EverTaskHandler<TestTaskRequestError>
{
    public override Task Handle(TestTaskRequestError backgroundTask, CancellationToken cancellationToken)
    {
        throw new Exception("Not executable handler");
    }
}

public class TestTaskConcurrent1Handler : EverTaskHandler<TestTaskConcurrent1>
{
    public override async Task Handle(TestTaskConcurrent1 backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        TestTaskConcurrent1.Counter = 1;
        TestTaskConcurrent1.EndTime =  DateTime.Now;
    }
}
public class TestTaskConcurrent2Handler : EverTaskHandler<TestTaskConcurrent2>
{
    public override async Task Handle(TestTaskConcurrent2 backgroundTask, CancellationToken cancellationToken)
    {
        TestTaskConcurrent2.StartTime = DateTime.Now;
        await Task.Delay(300, cancellationToken);
        TestTaskConcurrent2.Counter = 1;
        TestTaskConcurrent2.EndTime = DateTime.Now;
    }
}

public class TestTaskDelayed1Handler : EverTaskHandler<TestTaskDelayed1>
{
    public override async Task Handle(TestTaskDelayed1 backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        TestTaskDelayed1.Counter += 1;
    }
}


public class TestTaskDelayed2Handler : EverTaskHandler<TestTaskDelayed2>
{
    public override async Task Handle(TestTaskDelayed2 backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        TestTaskDelayed2.Counter += 1;
    }
}


public class TestTaskCpuboundHandler : EverTaskHandler<TestTaskCpubound>
{
    public TestTaskCpuboundHandler()
    {
        CpuBoundOperation = true;
    }
    public override async Task Handle(TestTaskCpubound backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        TestTaskCpubound.Counter=1;
    }
}

public class TestTaskWithRetryPolicyHandler : EverTaskHandler<TestTaskWithRetryPolicy>
{
    public override Task Handle(TestTaskWithRetryPolicy backgroundTask, CancellationToken cancellationToken)
    {
        TestTaskWithRetryPolicy.Counter++;

        if (TestTaskWithRetryPolicy.Counter < 3)
        {
            throw new Exception();
        }

        return Task.CompletedTask;
    }
}
public class TestTaskWithCustomRetryPolicyHanlder : EverTaskHandler<TestTaskWithCustomRetryPolicy>
{
    public TestTaskWithCustomRetryPolicyHanlder()
    {
        RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(100));
    }

    public override Task Handle(TestTaskWithCustomRetryPolicy backgroundTask, CancellationToken cancellationToken)
    {
        TestTaskWithCustomRetryPolicy.Counter++;

        if (TestTaskWithCustomRetryPolicy.Counter < 5)
        {
            throw new Exception();
        }

        return Task.CompletedTask;
    }
}

public class TestTaskWithCustomTimeoutHanlder : EverTaskHandler<TestTaskWithCustomTimeout>
{
    public TestTaskWithCustomTimeoutHanlder()
    {
        Timeout = TimeSpan.FromMilliseconds(300);
    }

    public override async Task Handle(TestTaskWithCustomTimeout backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);

        TestTaskWithCustomTimeout.Counter++;
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

using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Test tasks for delayed execution scenarios

public class TestTaskDelayed1() : IEverTask
{
    // Legacy static property for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
}

public class TestTaskDelayed2() : IEverTask
{
    // Legacy static property for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
}

public class TestTaskDelayed1Handler : EverTaskHandler<TestTaskDelayed1>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskDelayed1Handler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskDelayed1 backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskDelayed1.Counter += 1;
        _stateManager?.IncrementCounter(nameof(TestTaskDelayed1));
    }
}

public class TestTaskDelayed2Handler : EverTaskHandler<TestTaskDelayed2>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskDelayed2Handler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskDelayed2 backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskDelayed2.Counter += 1;
        _stateManager?.IncrementCounter(nameof(TestTaskDelayed2));
    }
}

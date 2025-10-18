using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Test tasks for CPU-bound operation scenarios

public class TestTaskCpubound() : IEverTask
{
    // Legacy static property for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
}

public class TestTaskCpuboundHandler : EverTaskHandler<TestTaskCpubound>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskCpuboundHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
        CpuBoundOperation = true;
    }

    public override async Task Handle(TestTaskCpubound backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskCpubound.Counter = 1;
        _stateManager?.IncrementCounter(nameof(TestTaskCpubound));
    }
}

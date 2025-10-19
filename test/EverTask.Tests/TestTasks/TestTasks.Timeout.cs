using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Test tasks for timeout scenarios

public class TestTaskWithCustomTimeout() : IEverTask
{
    // Legacy static property for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
}

public class TestTaskWithCustomTimeoutHanlder : EverTaskHandler<TestTaskWithCustomTimeout>
{
    private readonly TestTaskStateManager? _stateManager;

    public override TimeSpan? Timeout => TimeSpan.FromMilliseconds(300);

    public TestTaskWithCustomTimeoutHanlder(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskWithCustomTimeout backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskWithCustomTimeout.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskWithCustomTimeout));
    }
}

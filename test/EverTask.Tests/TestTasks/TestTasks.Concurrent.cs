using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Test tasks for concurrent execution scenarios

public class TestTaskConcurrent1() : IEverTask
{
    // Legacy static properties for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime { get; set; }
}

public class TestTaskConcurrent2() : IEverTask
{
    // Legacy static properties for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime { get; set; }
}

public class TestTaskConcurrent1Handler : EverTaskHandler<TestTaskConcurrent1>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskConcurrent1Handler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskConcurrent1 backgroundTask, CancellationToken cancellationToken)
    {
        // Record start using state manager if available
        _stateManager?.RecordStart(nameof(TestTaskConcurrent1));

        await Task.Delay(300, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskConcurrent1.Counter = 1;
        TestTaskConcurrent1.EndTime = DateTime.UtcNow;

        _stateManager?.RecordCompletion(nameof(TestTaskConcurrent1));
        _stateManager?.IncrementCounter(nameof(TestTaskConcurrent1));
    }
}

public class TestTaskConcurrent2Handler : EverTaskHandler<TestTaskConcurrent2>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskConcurrent2Handler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskConcurrent2 backgroundTask, CancellationToken cancellationToken)
    {
        // Record start using state manager if available
        TestTaskConcurrent2.StartTime = DateTime.UtcNow;
        _stateManager?.RecordStart(nameof(TestTaskConcurrent2));

        await Task.Delay(300, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskConcurrent2.Counter = 1;
        TestTaskConcurrent2.EndTime = DateTime.UtcNow;

        _stateManager?.RecordCompletion(nameof(TestTaskConcurrent2));
        _stateManager?.IncrementCounter(nameof(TestTaskConcurrent2));
    }
}

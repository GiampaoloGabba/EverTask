using EverTask.Resilience;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Test tasks for retry policy scenarios

public class TestTaskWithRetryPolicy() : IEverTask
{
    // Legacy static property for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
}

public class TestTaskWithCustomRetryPolicy() : IEverTask
{
    // Legacy static property for backward compatibility - will be phased out
    public static int Counter { get; set; } = 0;
}

public class TestTaskWithRetryPolicyHandler : EverTaskHandler<TestTaskWithRetryPolicy>
{
    private readonly TestTaskStateManager? _stateManager;

    // Configure retry policy: 3 attempts with short delays for testing
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(50));

    public TestTaskWithRetryPolicyHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override Task Handle(TestTaskWithRetryPolicy backgroundTask, CancellationToken cancellationToken)
    {
        // Update both static (legacy) and state manager (new approach)
        TestTaskWithRetryPolicy.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskWithRetryPolicy));

        if (TestTaskWithRetryPolicy.Counter < 3)
        {
            throw new Exception("Simulated failure for retry testing");
        }

        return Task.CompletedTask;
    }
}

public class TestTaskWithCustomRetryPolicyHanlder : EverTaskHandler<TestTaskWithCustomRetryPolicy>
{
    private readonly TestTaskStateManager? _stateManager;

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(100));

    public TestTaskWithCustomRetryPolicyHanlder(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override Task Handle(TestTaskWithCustomRetryPolicy backgroundTask, CancellationToken cancellationToken)
    {
        // Update both static (legacy) and state manager (new approach)
        TestTaskWithCustomRetryPolicy.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskWithCustomRetryPolicy));

        if (TestTaskWithCustomRetryPolicy.Counter < 5)
        {
            throw new Exception();
        }

        return Task.CompletedTask;
    }
}

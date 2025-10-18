using EverTask.Resilience;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Test tasks for recurring execution scenarios with various interval types

public class TestTaskRecurringSeconds() : IEverTask
{
    // Legacy static property for backward compatibility
    public static int Counter { get; set; } = 0;
}

public class TestTaskRecurringMinutes() : IEverTask
{
    // Legacy static property for backward compatibility
    public static int Counter { get; set; } = 0;
}

public class TestTaskRecurringWithFailure() : IEverTask
{
    // Legacy static property for backward compatibility
    public static int Counter { get; set; } = 0;
    public static int FailUntilCount { get; set; } = 2; // Fail first N attempts
}

public class TestTaskRecurringSecondsHandler : EverTaskHandler<TestTaskRecurringSeconds>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskRecurringSecondsHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskRecurringSeconds backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskRecurringSeconds.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskRecurringSeconds));
    }
}

public class TestTaskRecurringMinutesHandler : EverTaskHandler<TestTaskRecurringMinutes>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskRecurringMinutesHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskRecurringMinutes backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);

        // Update both static (legacy) and state manager (new approach)
        TestTaskRecurringMinutes.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskRecurringMinutes));
    }
}

public class TestTaskRecurringWithFailureHandler : EverTaskHandler<TestTaskRecurringWithFailure>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskRecurringWithFailureHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
        // Use linear retry policy with 3 attempts and short delays for testing
        RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(50));
    }

    public override async Task Handle(TestTaskRecurringWithFailure backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);

        // Update counter
        TestTaskRecurringWithFailure.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskRecurringWithFailure));

        // Fail until we reach the threshold
        if (TestTaskRecurringWithFailure.Counter <= TestTaskRecurringWithFailure.FailUntilCount)
        {
            throw new InvalidOperationException($"Simulated failure (attempt {TestTaskRecurringWithFailure.Counter})");
        }

        // After threshold, succeed
    }
}

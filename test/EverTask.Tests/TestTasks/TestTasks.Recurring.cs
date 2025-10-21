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

public record TestTaskDelayedRecurring(int delayMs) : IEverTask;

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

    // Use linear retry policy with 3 attempts and short delays for testing
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(50));

    public TestTaskRecurringWithFailureHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
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

public class TestTaskDelayedRecurringHandler : EverTaskHandler<TestTaskDelayedRecurring>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskDelayedRecurringHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskDelayedRecurring backgroundTask, CancellationToken cancellationToken)
    {
        // Simulate task execution time (delay)
        await Task.Delay(backgroundTask.delayMs, cancellationToken);
        _stateManager?.IncrementCounter(nameof(TestTaskDelayedRecurring));
    }
}


// Test tasks for queue sharding - each task has its handler with specific QueueName
public class TestTaskRecurringQueueShard1() : IEverTask
{
    public static int Counter { get; set; } = 0;
}

public class TestTaskRecurringQueueShard2() : IEverTask
{
    public static int Counter { get; set; } = 0;
}

public class TestTaskRecurringQueueShard1Handler : EverTaskHandler<TestTaskRecurringQueueShard1>
{
    private readonly TestTaskStateManager? _stateManager;

    // Set QueueName to route this task to "shard1" queue
    public override string? QueueName => "shard1";

    public TestTaskRecurringQueueShard1Handler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskRecurringQueueShard1 backgroundTask, CancellationToken
                                          cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        TestTaskRecurringQueueShard1.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskRecurringQueueShard1));
    }
}

public class TestTaskRecurringQueueShard2Handler : EverTaskHandler<TestTaskRecurringQueueShard2>
{
    private readonly TestTaskStateManager? _stateManager;

    // Set QueueName to route this task to "shard2" queue
    public override string? QueueName => "shard2";

    public TestTaskRecurringQueueShard2Handler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskRecurringQueueShard2 backgroundTask, CancellationToken
                                          cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        TestTaskRecurringQueueShard2.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskRecurringQueueShard2));
    }
}

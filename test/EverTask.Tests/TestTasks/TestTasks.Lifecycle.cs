using EverTask.Resilience;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Test tasks for lifecycle callback scenarios

public class TestTaskLifecycle() : IEverTask
{
    public static List<string> CallbackOrder { get; set; } = new();
    public static Guid? LastTaskId { get; set; }
}

public class TestTaskLifecycleWithError() : IEverTask
{
    public static List<string> CallbackOrder { get; set; } = new();
    public static Guid? LastTaskId { get; set; }
    public static string? LastErrorMessage { get; set; }
    public static Exception? LastException { get; set; }
}

public class TestTaskLifecycleWithAsyncDispose() : IEverTask
{
    public static List<string> CallbackOrder { get; set; } = new();
    public static bool WasDisposed { get; set; }
}

public class TestTaskLazyModeRecurringWithAsyncDispose() : IEverTask
{
    public static int DisposeCount { get; set; }
    public static int ExecutionCount { get; set; }
    public static readonly object LockObject = new();
}

public class TestTaskLazyModeDelayedWithAsyncDispose() : IEverTask
{
    public static List<string> CallbackOrder { get; set; } = new();
    public static bool WasDisposed { get; set; }
    public static bool WasDisposedDuringDispatch { get; set; }
}

public class TestTaskLifecycleHandler : EverTaskHandler<TestTaskLifecycle>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskLifecycleHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override ValueTask OnStarted(Guid taskId)
    {
        TestTaskLifecycle.CallbackOrder.Add("OnStarted");
        TestTaskLifecycle.LastTaskId = taskId;
        return ValueTask.CompletedTask;
    }

    public override async Task Handle(TestTaskLifecycle backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        TestTaskLifecycle.CallbackOrder.Add("Handle");
        _stateManager?.IncrementCounter(nameof(TestTaskLifecycle));
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        TestTaskLifecycle.CallbackOrder.Add("OnCompleted");
        return ValueTask.CompletedTask;
    }
}

public class TestTaskLifecycleWithErrorHandler : EverTaskHandler<TestTaskLifecycleWithError>
{
    private readonly TestTaskStateManager? _stateManager;

    // Use single attempt (no retries) for predictable callback order
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public TestTaskLifecycleWithErrorHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override ValueTask OnStarted(Guid taskId)
    {
        TestTaskLifecycleWithError.CallbackOrder.Add("OnStarted");
        TestTaskLifecycleWithError.LastTaskId = taskId;
        return ValueTask.CompletedTask;
    }

    public override async Task Handle(TestTaskLifecycleWithError backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        TestTaskLifecycleWithError.CallbackOrder.Add("Handle");
        _stateManager?.IncrementCounter(nameof(TestTaskLifecycleWithError));

        // Throw an exception to trigger OnError
        throw new InvalidOperationException("Test error for lifecycle callback");
    }

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        TestTaskLifecycleWithError.CallbackOrder.Add("OnRetry");
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        TestTaskLifecycleWithError.CallbackOrder.Add("OnError");
        TestTaskLifecycleWithError.LastErrorMessage = message;
        TestTaskLifecycleWithError.LastException = exception;
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        // Should not be called when there's an error
        TestTaskLifecycleWithError.CallbackOrder.Add("OnCompleted");
        return ValueTask.CompletedTask;
    }
}

public class TestTaskLifecycleWithAsyncDisposeHandler : EverTaskHandler<TestTaskLifecycleWithAsyncDispose>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskLifecycleWithAsyncDisposeHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskLifecycleWithAsyncDispose backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        TestTaskLifecycleWithAsyncDispose.CallbackOrder.Add("Handle");
        _stateManager?.IncrementCounter(nameof(TestTaskLifecycleWithAsyncDispose));
    }

    protected override ValueTask DisposeAsyncCore()
    {
        TestTaskLifecycleWithAsyncDispose.CallbackOrder.Add("DisposeAsyncCore");
        TestTaskLifecycleWithAsyncDispose.WasDisposed = true;
        return ValueTask.CompletedTask;
    }
}

public class TestTaskLazyModeRecurringWithAsyncDisposeHandler : EverTaskHandler<TestTaskLazyModeRecurringWithAsyncDispose>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskLazyModeRecurringWithAsyncDisposeHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskLazyModeRecurringWithAsyncDispose backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);

        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount++;
        }

        _stateManager?.IncrementCounter(nameof(TestTaskLazyModeRecurringWithAsyncDispose));
    }

    protected override ValueTask DisposeAsyncCore()
    {
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount++;
        }

        return ValueTask.CompletedTask;
    }
}

public class TestTaskLazyModeDelayedWithAsyncDisposeHandler : EverTaskHandler<TestTaskLazyModeDelayedWithAsyncDispose>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskLazyModeDelayedWithAsyncDisposeHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskLazyModeDelayedWithAsyncDispose backgroundTask, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Add("Handle");
        _stateManager?.IncrementCounter(nameof(TestTaskLazyModeDelayedWithAsyncDispose));
    }

    protected override ValueTask DisposeAsyncCore()
    {
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Add("DisposeAsyncCore");

        // This should only be called after execution, not during dispatch
        // If called during dispatch, the task hasn't executed yet
        if (TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Count == 0)
        {
            TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch = true;
        }

        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed = true;
        return ValueTask.CompletedTask;
    }
}

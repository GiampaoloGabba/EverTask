using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

// Multi-queue test tasks and handlers

/// <summary>
/// Task that should be routed to high-priority queue
/// </summary>
public class TestTaskHighPriority : IEverTask
{
    public static int Counter { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime { get; set; }
}

public class TestTaskHighPriorityHandler : EverTaskHandler<TestTaskHighPriority>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskHighPriorityHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override string? QueueName => "high-priority";

    public override async Task Handle(TestTaskHighPriority task, CancellationToken ct)
    {
        _stateManager?.RecordStart(nameof(TestTaskHighPriority));
        TestTaskHighPriority.StartTime = DateTime.UtcNow;

        await Task.Delay(100, ct);

        TestTaskHighPriority.Counter++;
        TestTaskHighPriority.EndTime = DateTime.UtcNow;
        _stateManager?.RecordCompletion(nameof(TestTaskHighPriority));
        _stateManager?.IncrementCounter(nameof(TestTaskHighPriority));
    }
}

/// <summary>
/// Task that should be routed to background queue
/// </summary>
public class TestTaskBackground : IEverTask
{
    public static int Counter { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime { get; set; }
}

public class TestTaskBackgroundHandler : EverTaskHandler<TestTaskBackground>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskBackgroundHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override string? QueueName => "background";

    public override async Task Handle(TestTaskBackground task, CancellationToken ct)
    {
        _stateManager?.RecordStart(nameof(TestTaskBackground));
        TestTaskBackground.StartTime = DateTime.UtcNow;

        await Task.Delay(100, ct);

        TestTaskBackground.Counter++;
        TestTaskBackground.EndTime = DateTime.UtcNow;
        _stateManager?.RecordCompletion(nameof(TestTaskBackground));
        _stateManager?.IncrementCounter(nameof(TestTaskBackground));
    }
}

/// <summary>
/// Task that should be routed to default queue (no QueueName specified)
/// </summary>
public class TestTaskDefaultQueue : IEverTask
{
    public static int Counter { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime { get; set; }
}

public class TestTaskDefaultQueueHandler : EverTaskHandler<TestTaskDefaultQueue>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskDefaultQueueHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    // No QueueName specified - should route to "default"

    public override async Task Handle(TestTaskDefaultQueue task, CancellationToken ct)
    {
        _stateManager?.RecordStart(nameof(TestTaskDefaultQueue));
        TestTaskDefaultQueue.StartTime = DateTime.UtcNow;

        await Task.Delay(100, ct);

        TestTaskDefaultQueue.Counter++;
        TestTaskDefaultQueue.EndTime = DateTime.UtcNow;
        _stateManager?.RecordCompletion(nameof(TestTaskDefaultQueue));
        _stateManager?.IncrementCounter(nameof(TestTaskDefaultQueue));
    }
}

/// <summary>
/// Task for testing parallel execution in high-parallelism queue
/// </summary>
public class TestTaskParallel : IEverTask
{
    public string Id { get; init; } = TestGuidGenerator.New().ToString();
}

public class TestTaskParallelHandler : EverTaskHandler<TestTaskParallel>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskParallelHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override string? QueueName => "parallel";

    public override async Task Handle(TestTaskParallel task, CancellationToken ct)
    {
        _stateManager?.RecordStart($"TestTaskParallel_{task.Id}");

        // Simulate some work
        await Task.Delay(200, ct);

        _stateManager?.RecordCompletion($"TestTaskParallel_{task.Id}");
        _stateManager?.IncrementCounter("TestTaskParallel_Total");
    }
}

/// <summary>
/// Task for testing sequential execution in low-parallelism queue
/// </summary>
public class TestTaskSequential : IEverTask
{
    public string Id { get; init; } = TestGuidGenerator.New().ToString();
}

public class TestTaskSequentialHandler : EverTaskHandler<TestTaskSequential>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskSequentialHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override string? QueueName => "sequential";

    public override async Task Handle(TestTaskSequential task, CancellationToken ct)
    {
        _stateManager?.RecordStart($"TestTaskSequential_{task.Id}");

        // Simulate some work
        await Task.Delay(200, ct);

        _stateManager?.RecordCompletion($"TestTaskSequential_{task.Id}");
        _stateManager?.IncrementCounter("TestTaskSequential_Total");
    }
}

using System.Collections.Concurrent;
using EverTask.Resilience;

namespace EverTask.Tests;

// Test tasks and shared state for the M2 config-validation / non-throwing-log scenarios
// (zero-consumer clamp, Default-queue fallback backpressure, Drop* loss, log render faults,
// OnError-override faults).

public sealed class ConfigValidationState
{
    /// <summary>Indexes of executed counter/drop tasks.</summary>
    public ConcurrentBag<int> Executed { get; } = new();

    /// <summary>Released by blocking handlers when they start executing.</summary>
    public SemaphoreSlim BlockingEntered { get; } = new(0, int.MaxValue);

    /// <summary>Blocking handlers wait on this gate until the test releases them.</summary>
    public SemaphoreSlim BlockingGate { get; } = new(0, int.MaxValue);
}

/// <summary>Fast counter task (default queue). Used by the zero-consumer clamp test.</summary>
public record ConfigCounterTask(int Index) : IEverTask;

public class ConfigCounterTaskHandler(ConfigValidationState state) : EverTaskHandler<ConfigCounterTask>
{
    public override Task Handle(ConfigCounterTask backgroundTask, CancellationToken cancellationToken)
    {
        state.Executed.Add(backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>Blocking task on the DEFAULT queue (no QueueName override).</summary>
public record ConfigBlockingTask : IEverTask;

public class ConfigBlockingTaskHandler(ConfigValidationState state) : EverTaskHandler<ConfigBlockingTask>
{
    public override async Task Handle(ConfigBlockingTask backgroundTask, CancellationToken cancellationToken)
    {
        state.BlockingEntered.Release();
        await state.BlockingGate.WaitAsync(cancellationToken);
    }
}

/// <summary>Blocking task routed to the "dropq" queue (Drop* full mode test).</summary>
public record ConfigDropTask(int Index) : IEverTask;

public class ConfigDropTaskHandler(ConfigValidationState state) : EverTaskHandler<ConfigDropTask>
{
    public override string? QueueName => "dropq";

    public override async Task Handle(ConfigDropTask backgroundTask, CancellationToken cancellationToken)
    {
        state.BlockingEntered.Release();
        await state.BlockingGate.WaitAsync(cancellationToken);
        state.Executed.Add(backgroundTask.Index);
    }
}

/// <summary>Handler that logs a malformed format specifier / alignment via its own Logger.</summary>
public record ConfigBadLogTask : IEverTask;

public class ConfigBadLogTaskHandler : EverTaskHandler<ConfigBadLogTask>
{
    public override Task Handle(ConfigBadLogTask backgroundTask, CancellationToken cancellationToken)
    {
        // 'Q' is an invalid format specifier on an IFormattable arg (FormatException); the second
        // call hits Math.Abs(int.MinValue) on the alignment (OverflowException). A handler's own
        // logging must never fault the task.
        Logger.LogInformation("bad format {0:Q}", 42);
        Logger.LogWarning("bad alignment {0,-2147483648}", 7);
        return Task.CompletedTask;
    }
}

/// <summary>Handler whose Handle fails AND whose OnError override throws.</summary>
public record ConfigOnErrorThrowsTask : IEverTask;

public class ConfigOnErrorThrowsTaskHandler : EverTaskHandler<ConfigOnErrorThrowsTask>
{
    public const string RealFailureMarker = "REAL-FAILURE-CU18";

    // Minimal retry so the terminal-failure path is reached quickly (OnError runs once, at the end).
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(1));

    public override Task Handle(ConfigOnErrorThrowsTask backgroundTask, CancellationToken cancellationToken)
        => throw new InvalidOperationException(RealFailureMarker);

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
        => throw new Exception("onerror-override-boom");
}

using System.Collections.Concurrent;

namespace EverTask.Tests;

// Test tasks and shared state for queue-resilience scenarios
// (startup recovery, full queues, scheduler head-of-line blocking).

/// <summary>
/// Shared, test-local coordination state. Register a fresh instance per test host
/// so handlers can signal/await the test without static state pollution.
/// </summary>
public class ResilienceTestState
{
    /// <summary>Indexes of executed <see cref="ResilienceCounterTask"/> instances.</summary>
    public ConcurrentBag<int> ExecutedIndexes { get; } = new();

    /// <summary>Signaled by blocking handlers when they start executing.</summary>
    public SemaphoreSlim BlockingEntered { get; } = new(0, int.MaxValue);

    /// <summary>Blocking handlers wait on this gate until the test releases them.</summary>
    public SemaphoreSlim BlockingGate { get; } = new(0, int.MaxValue);

    /// <summary>Completion count of blocking tasks (after the gate is released).</summary>
    public int BlockingCompleted;

    /// <summary>Completion count of tasks executed on the default queue.</summary>
    public int DefaultQueueCompleted;

    /// <summary>Payload strings received by <see cref="LegacyPayloadProbeTask"/> handlers (B4 recovery).</summary>
    public ConcurrentBag<string> CapturedPayloads { get; } = new();
}

/// <summary>
/// B4 probe: carries a string payload so a legacy Newtonsoft-serialized row (non-ASCII + 4-byte emoji) can
/// be recovered end-to-end and the exact payload string asserted at the handler.
/// </summary>
public record LegacyPayloadProbeTask(string Text) : IEverTask;

public class LegacyPayloadProbeTaskHandler(ResilienceTestState state) : EverTaskHandler<LegacyPayloadProbeTask>
{
    public override Task Handle(LegacyPayloadProbeTask backgroundTask, CancellationToken cancellationToken)
    {
        state.CapturedPayloads.Add(backgroundTask.Text);
        return Task.CompletedTask;
    }
}

// --- First-class declarative polymorphism support (nested polymorphic payload property) ---
// The supported way to carry a polymorphic value in a task payload: annotate the base type with STJ's
// [JsonPolymorphic] + [JsonDerivedType] (a CLOSED, declared discriminator set — NOT arbitrary type loading,
// so the L33 isolation invariant holds). EverTaskJson round-trips it with no core change.

[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(EmailChannel), "email")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SmsChannel), "sms")]
public abstract class NotifyChannel { }

public sealed class EmailChannel : NotifyChannel { public string Address { get; set; } = ""; }
public sealed class SmsChannel   : NotifyChannel { public string Number  { get; set; } = ""; }

/// <summary>Probe with a NESTED polymorphic property (declarative discriminator). Persisted then recovered to
/// prove the concrete subtype + its members survive the full serialize→store→deserialize→execute chain.</summary>
public record PolymorphicNotifyTask(NotifyChannel Channel) : IEverTask;

public class PolymorphicNotifyTaskHandler(ResilienceTestState state) : EverTaskHandler<PolymorphicNotifyTask>
{
    public override Task Handle(PolymorphicNotifyTask backgroundTask, CancellationToken cancellationToken)
    {
        var captured = backgroundTask.Channel switch
        {
            EmailChannel e => $"email:{e.Address}",
            SmsChannel s   => $"sms:{s.Number}",
            _              => $"unknown:{backgroundTask.Channel.GetType().Name}"
        };
        state.CapturedPayloads.Add(captured);
        return Task.CompletedTask;
    }
}

/// <summary>Fast task that records its index in the shared state.</summary>
public record ResilienceCounterTask(int Index) : IEverTask;

public class ResilienceCounterTaskHandler(ResilienceTestState state) : EverTaskHandler<ResilienceCounterTask>
{
    public override Task Handle(ResilienceCounterTask backgroundTask, CancellationToken cancellationToken)
    {
        state.ExecutedIndexes.Add(backgroundTask.Index);
        return Task.CompletedTask;
    }
}

/// <summary>Task that blocks its consumer until the test releases the gate. Runs on the "blocked" queue.</summary>
public record ResilienceBlockingTask : IEverTask;

public class ResilienceBlockingTaskHandler(ResilienceTestState state) : EverTaskHandler<ResilienceBlockingTask>
{
    public override string? QueueName => "blocked";

    public override async Task Handle(ResilienceBlockingTask backgroundTask, CancellationToken cancellationToken)
    {
        state.BlockingEntered.Release();
        await state.BlockingGate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref state.BlockingCompleted);
    }
}

/// <summary>Fast task routed to the default queue (control group for head-of-line blocking).</summary>
public record ResilienceDefaultQueueTask : IEverTask;

public class ResilienceDefaultQueueTaskHandler(ResilienceTestState state) : EverTaskHandler<ResilienceDefaultQueueTask>
{
    public override Task Handle(ResilienceDefaultQueueTask backgroundTask, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref state.DefaultQueueCompleted);
        return Task.CompletedTask;
    }
}

/// <summary>Fast recurring task counting executions.</summary>
public record ResilienceRecurringTask : IEverTask;

public class ResilienceRecurringTaskHandler(ResilienceTestState state) : EverTaskHandler<ResilienceRecurringTask>
{
    public override Task Handle(ResilienceRecurringTask backgroundTask, CancellationToken cancellationToken)
    {
        state.ExecutedIndexes.Add(-1);
        return Task.CompletedTask;
    }
}

using System.Collections.Concurrent;

namespace EverTask.Worker;

/// <summary>
/// Per-process registry of task deliveries in flight: a persistence id is registered from the
/// moment it is written to a queue channel until its delivery terminally ends (execution
/// finished, re-parked to the scheduler, dropped). A second write of the same id while a
/// delivery is registered is rejected at the channel boundary
/// (<see cref="EnqueueResult.DuplicateInProcess"/>), which makes in-process double delivery
/// impossible by construction — regardless of timing — for any pair of writers (live dispatch,
/// startup recovery, scheduler slot fires, taskKey re-dispatch).
/// </summary>
/// <remarks>
/// <para>
/// <strong>End discipline (the invariant that keeps this sound):</strong> every registration is
/// ended exactly once, by exactly one of:
/// (1) the single outer <c>finally</c> of <c>WorkerExecutor.DoWork</c> — the LAST act of every
/// consumed delivery, covering all exit paths (terminal completion, rate-limit deferral, retry
/// re-park, gate rejection, blacklist drop) with no per-path enumeration;
/// (2) the enqueue rollback paths of <c>WorkerQueue</c> (storage or channel-write failure before
/// the task ever became consumable);
/// (3) the bounded channel's <c>itemDropped</c> callback (Drop* full modes).
/// Because the End is the last act of a delivery, a successor delivery of the same id can only
/// register after it — no delivery can ever release a successor's registration.
/// </para>
/// <para>
/// Size is structurally bounded: at most (channel capacities + executing tasks) entries, and the
/// set empties itself as deliveries finish. Dispatch-only processes are bounded by their channel
/// capacity. No cap, no time-based lifecycle.
/// </para>
/// </remarks>
public sealed class TaskDeliveryRegistry
{
    private readonly ConcurrentDictionary<Guid, byte> _deliveries = new();

    /// <summary>
    /// Registers a delivery for the id. Returns false when a delivery of the same id is already
    /// in flight in this process (the caller must NOT write the task to a channel).
    /// </summary>
    public bool TryBegin(Guid persistenceId) => _deliveries.TryAdd(persistenceId, 0);

    /// <summary>
    /// Ends the delivery for the id. Must be called exactly once per successful
    /// <see cref="TryBegin"/>, per the end discipline documented on the class.
    /// </summary>
    public void End(Guid persistenceId) => _deliveries.TryRemove(persistenceId, out _);

    /// <summary>Returns whether a delivery of the id is currently in flight in this process.</summary>
    public bool IsDelivering(Guid persistenceId) => _deliveries.ContainsKey(persistenceId);

    /// <summary>Current number of in-flight deliveries (diagnostics).</summary>
    public int Count => _deliveries.Count;
}

namespace EverTask.Dispatcher;

/// <summary>
/// The slice of a PERSISTED row a re-dispatch must work from verbatim, instead of letting it be re-derived
/// from the handler type or read back from the store.
/// </summary>
/// <remarks>
/// Recovery rebuilds an executor from a row that was written by an earlier process: its occurrence identity
/// (<paramref name="ParentTaskId"/>, <paramref name="RuntimeInfo"/>), the schedule version it belongs to and
/// the queue it was actually routed to are facts of that row, not properties of the handler. Re-deriving them
/// silently changes them — a recovered occurrence would come back parentless, at version 0, on whatever queue
/// the handler attribute names today, while the recovery loop itself groups the row by its STORED queue.
/// <para>
/// <paramref name="Status"/> and <paramref name="ScheduleVersion"/> additionally carry the compare-and-swap
/// expectations of the exhausted-series finalization: the decision is computed from THIS row, so this is what
/// the conditional write must expect. Reading them back at write time would absorb any concurrent
/// <c>Cancel</c> into the expectation and overwrite the status the user chose.
/// </para>
/// <para>
/// <paramref name="RunNumber"/> is the 1-based run the delivery represents: the row's <c>CurrentRunCount</c>
/// plus one, since the counter is only incremented once a run is over. The dispatcher fills it in for every
/// dispatch (a task with no history is run 1), so the executing handler can read it without a round-trip and a
/// recovered series keeps counting from where the row says it was. An OCCURRENCE arrives with it already set,
/// out of its own <c>RuntimeInfo</c>: the counter of a one-shot child says nothing about which run of the
/// series it is, so deriving it there would report every recovered occurrence as run 1.
/// </para>
/// <para>
/// <paramref name="NominalSlotUtc"/> is the slot an occurrence stands for, likewise out of its
/// <c>RuntimeInfo</c>. Carrying it explicitly is what keeps the answer durable: the executor's
/// <c>ExecutionTime</c> says when the scheduler fires the delivery, and the rate-limit gate replaces it with
/// the slot it reserved.
/// </para>
/// <para>
/// It travels as an internal type through an internal member of <see cref="ITaskDispatcherInternal"/> — which
/// <c>Dispatcher</c> implements explicitly — so the dispatcher's public surface keeps the exact shape the
/// previous release shipped.
/// </para>
/// </remarks>
internal readonly record struct DispatchRowMetadata(
    Guid? ParentTaskId,
    string? RuntimeInfo,
    int ScheduleVersion,
    string? QueueName,
    QueuedTaskStatus? Status = null,
    int? RunNumber = null,
    DateTimeOffset? NominalSlotUtc = null)
{
    /// <summary>A dispatch that owns no persisted row yet: everything is derived as usual.</summary>
    public static readonly DispatchRowMetadata None = default;
}

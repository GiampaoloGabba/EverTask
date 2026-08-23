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
/// It travels as an internal type through an internal member of <see cref="ITaskDispatcherInternal"/> — which
/// <c>Dispatcher</c> implements explicitly — so the dispatcher's public surface keeps the exact shape the
/// previous release shipped (P6/X6).
/// </para>
/// </remarks>
internal readonly record struct DispatchRowMetadata(
    Guid? ParentTaskId,
    string? RuntimeInfo,
    int ScheduleVersion,
    string? QueueName,
    QueuedTaskStatus? Status = null)
{
    /// <summary>A dispatch that owns no persisted row yet: everything is derived as usual.</summary>
    public static readonly DispatchRowMetadata None = default;
}

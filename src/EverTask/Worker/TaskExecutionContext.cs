namespace EverTask.Worker;

/// <summary>
/// The per-delivery execution context handed to the handler. Immutable to the handler; the worker owns the one
/// field that legitimately changes while a delivery is alive, the attempt number.
/// </summary>
internal sealed class TaskExecutionContext : ITaskExecutionContext
{
    private int _attempt = 1;

    public required Guid            TaskId           { get; init; }
    public          Guid?           ScheduleId       { get; init; }
    public          string?         TaskKey          { get; init; }
    public          DateTimeOffset? ScheduledAtUtc   { get; init; }
    public          DateTimeOffset? ScheduledAtLocal { get; init; }
    public          string?         TimeZoneId       { get; init; }
    public required DateTimeOffset  StartedAtUtc     { get; init; }
    public          int             RunNumber        { get; init; } = 1;
    public          int             ScheduleVersion  { get; init; }
    public          bool            IsRecurring      { get; init; }
    public          bool            IsOccurrence     { get; init; }
    public          MisfireInfo?    Misfire          { get; init; }

    /// <remarks>
    /// Read from the handler's own flow while the worker writes it from the retry loop of the same delivery:
    /// volatile access is what makes the write visible without a lock on the hot path.
    /// </remarks>
    public int Attempt => Volatile.Read(ref _attempt);

    internal void SetAttempt(int attempt) => Volatile.Write(ref _attempt, attempt);

    /// <summary>
    /// Builds the context of one delivery from the executor that carries it.
    /// </summary>
    /// <param name="task">The executor being delivered.</param>
    /// <param name="startedAtUtc">The scheduling clock's "now" at the start of the delivery.</param>
    /// <param name="misfireThreshold">How late a delivery may start before it counts as a misfire.</param>
    internal static TaskExecutionContext Create(TaskHandlerExecutor task, DateTimeOffset startedAtUtc,
                                                TimeSpan misfireThreshold)
    {
        var slot = task.NominalSlotOfDelivery;

        // Keep one consistent occurrence snapshot for the three values derived from it below.
        var occurrence = task.RowOccurrence;

        // The zone is part of the SCHEDULE. An inline delivery carries the definition and reads it there; an
        // OCCURRENCE carries none by construction — it belongs to its series through its parent — so the zone
        // was stamped on its row when it was materialized and comes back from there. A schedule without one
        // reports null on both, which is exactly how a handler reads "plain UTC" — and how every delivery read
        // before zones existed.
        var timeZoneId = task.RecurringTask?.TimeZoneId ?? occurrence?.TimeZoneId;

        return new TaskExecutionContext
        {
            TaskId           = task.PersistenceId,
            ScheduleId       = task.ParentTaskId,
            TaskKey          = task.TaskKey,
            ScheduledAtUtc   = slot,
            ScheduledAtLocal = ScheduleTimeZone.ToLocalTime(timeZoneId, slot),
            TimeZoneId       = timeZoneId,
            StartedAtUtc     = startedAtUtc,
            // The durable run number travels on the executor: the storage counter is only incremented AFTER a
            // run, so reading it here would need a round-trip on the hot path AND report one run too few. An
            // occurrence's number comes from its row's own occurrence metadata instead (C1) — stamped where
            // the row became a task, or read back from the row here when it was not; the counter of a
            // one-shot child would report every one of them as run 1.
            RunNumber       = task.RunNumber ?? occurrence?.RunNumber ?? 1,
            ScheduleVersion = task.ScheduleVersion,
            // An occurrence carries no definition of its own — it belongs to the series through its parent.
            IsRecurring  = task.RecurringTask != null || task.ParentTaskId != null,
            IsOccurrence = task.ParentTaskId != null,
            Misfire      = DetectMisfire(occurrence, slot, startedAtUtc, misfireThreshold)
        };
    }

    /// <summary>
    /// What this delivery should report about its own lateness: the replay it belongs to when its row records
    /// one, otherwise the plain "started later than the threshold allows".
    /// </summary>
    /// <remarks>
    /// A materialized occurrence carries the DECISION that created it — which run of missed slots it stands
    /// for, and how many — because by the time it runs that backlog no longer exists to be measured. Lateness
    /// is added here either way: only the delivery knows when it actually started.
    /// <para>
    /// A delivery with no slot (an immediate task) can never be late, and one within the threshold with no
    /// recorded replay reports nothing at all: null is the normal case, so a handler that does not care about
    /// lateness never has to inspect a kind.
    /// </para>
    /// </remarks>
    private static MisfireInfo? DetectMisfire(OccurrenceRuntimeInfo? occurrence, DateTimeOffset? slot,
                                              DateTimeOffset startedAtUtc, TimeSpan misfireThreshold)
    {
        if (slot == null)
            return null;

        var lateness = startedAtUtc - slot.Value;

        if (occurrence is { MisfireKind: { } kind })
        {
            return new MisfireInfo
            {
                Kind               = kind,
                MissedFromUtc      = occurrence.MissedFromUtc,
                MissedThroughUtc   = occurrence.MissedThroughUtc,
                MissedCount        = occurrence.MissedCount ?? 0,
                MissedCountIsExact = occurrence.MissedCountIsExact ?? true,
                Lateness           = lateness
            };
        }

        return lateness > misfireThreshold
                   ? new MisfireInfo { Kind = MisfireKind.Late, Lateness = lateness }
                   : null;
    }
}

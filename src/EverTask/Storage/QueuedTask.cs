namespace EverTask.Storage;

public class QueuedTask
{
    public Guid            Id                    { get; set; }
    public DateTimeOffset  CreatedAtUtc          { get; set; }
    public DateTimeOffset? LastExecutionUtc      { get; set; }
    public double          ExecutionTimeMs       { get; set; }
    public DateTimeOffset? ScheduledExecutionUtc { get; set; }
    public string          Type                  { get; set; } = "";
    public string          Request               { get; set; } = "";
    public string          Handler               { get; set; } = "";
    public string?         Exception             { get; set; }
    public bool            IsRecurring           { get; set; }
    public string?         RecurringTask         { get; set; }
    public string?         RecurringInfo         { get; set; }
    public int?            CurrentRunCount       { get; set; }
    public int?            MaxRuns               { get; set; }
    public DateTimeOffset? RunUntil              { get; set; }
    public DateTimeOffset? NextRunUtc            { get; set; }
    public string?         QueueName             { get; set; }
    public string?         TaskKey               { get; set; }
    public int?            AuditLevel            { get; set; }

    /// <summary>
    /// Number of consecutive failed startup-recovery re-dispatch attempts. Incremented each time
    /// recovery fails to re-dispatch this task; once it reaches the configured limit the task is
    /// poisoned (marked <see cref="QueuedTaskStatus.Failed"/>) so a persistent failure stops being
    /// retried at every restart. Reset after a successful re-dispatch. Not part of the recoverable
    /// predicate.
    /// </summary>
    public int?            RecoveryDispatchFailureCount { get; set; }

    /// <summary>
    /// Identifier of the recurring schedule row this task is an occurrence of, or <c>null</c> for a
    /// schedule row and for every ordinary one-shot task. A child occurrence always carries a
    /// <see cref="ScheduledExecutionUtc"/> (its nominal slot), enforced by a check constraint, and the
    /// pair is unique so the same slot can never be materialized twice.
    /// </summary>
    public Guid? ParentTaskId { get; set; }

    /// <summary>
    /// Free-form JSON with the runtime state of this row: occurrence metadata on a child, schedule
    /// runtime state (for example a halted catch-up) on a durable schedule row. Opaque to storage.
    /// </summary>
    public string? RuntimeInfo { get; set; }

    /// <summary>
    /// Monotonic version of the schedule definition, bumped by every runtime reschedule. Advances of a
    /// versioned schedule carry the expected value so a completion racing a reschedule loses the
    /// compare-and-swap and recomputes instead of overwriting the new definition. Legacy rows stay at 0.
    /// </summary>
    public int ScheduleVersion { get; set; }

    public QueuedTaskStatus         Status       { get; set; }
    public ICollection<StatusAudit> StatusAudits { get; set; } = new List<StatusAudit>();
    public ICollection<RunsAudit>   RunsAudits   { get; set; } = new List<RunsAudit>();

    /// <summary>The schedule row this occurrence belongs to (navigation for the self-referencing FK).</summary>
    public QueuedTask? Parent { get; set; }

    /// <summary>The materialized occurrences of this schedule row (navigation for the self-referencing FK).</summary>
    public ICollection<QueuedTask> Occurrences { get; set; } = new List<QueuedTask>();

    /// <summary>
    /// Collection of execution logs captured during task execution.
    /// Only populated if log capture is enabled in configuration.
    /// </summary>
    public ICollection<TaskExecutionLog> ExecutionLogs { get; set; } = new List<TaskExecutionLog>();

    /// <summary>
    /// Canonical recoverable predicate (client-side) shared by every storage provider: a task is
    /// recoverable on restart, and may be re-queued by <c>TrySetQueuedIfRecoverable</c>, only while
    /// it has runs left (<see cref="MaxRuns"/>), still has a slot to run before its
    /// <see cref="RunUntil"/>, and sits in a non-terminal status (or is a recurring task between two
    /// runs).
    /// <para>
    /// THE single source of truth for the client-side evaluation. The EF Core server-side queries
    /// (<c>RetrievePending</c> / <c>TrySetQueuedIfRecoverable</c>) mirror this as a translatable
    /// LINQ expression — keep them in sync with this method.
    /// </para>
    /// </summary>
    public bool IsRecoverable(DateTimeOffset now) => IsRecoverableForExecution(now);

    /// <summary>
    /// Category (i) of the recovery filter: rows that still have work to EXECUTE.
    /// </summary>
    /// <remarks>
    /// Status and <see cref="MaxRuns"/> are ANDed in front of everything and are never bypassed. The
    /// temporal term is grouped so a recurring series whose <see cref="RunUntil"/> elapsed DURING a
    /// downtime still recovers the slots it had already scheduled before that boundary
    /// (<c>NextRunUtc &lt; RunUntil</c>): without that branch the pending occurrence was silently lost and
    /// the row stayed <c>Queued</c> forever. The three server-side copies of this expression
    /// (<c>EfCoreTaskStorage.RecoverableQuery</c>, the inline SQLite list and the Postgres partial index)
    /// must group it identically.
    /// </remarks>
    public bool IsRecoverableForExecution(DateTimeOffset now) =>
        // < MaxRuns (not <=): a series at CurrentRunCount == MaxRuns is exhausted and terminal, matching
        // CalculateNextRun's `currentRun >= MaxRuns`. null CurrentRunCount counts as 0.
        (MaxRuns == null || (CurrentRunCount ?? 0) < MaxRuns)
        && (Status is QueuedTaskStatus.WaitingQueue or QueuedTaskStatus.Queued
                or QueuedTaskStatus.Pending or QueuedTaskStatus.ServiceStopped
                or QueuedTaskStatus.InProgress
            || (IsRecurring && NextRunUtc != null &&
                Status is QueuedTaskStatus.Completed or QueuedTaskStatus.Failed))
        && (RunUntil == null
            || RunUntil >= now
            || (IsRecurring && NextRunUtc != null && RunUntil != null && NextRunUtc < RunUntil));

    /// <summary>
    /// True while <paramref name="status"/> can still lead to an execution. The terminal statuses
    /// (<see cref="QueuedTaskStatus.Completed"/>, <see cref="QueuedTaskStatus.Failed"/>,
    /// <see cref="QueuedTaskStatus.Cancelled"/>) are the only ones that free an occurrence's slot in the
    /// concurrency budget of its schedule.
    /// </summary>
    public static bool IsNonTerminalStatus(QueuedTaskStatus status) =>
        status is QueuedTaskStatus.WaitingQueue or QueuedTaskStatus.Queued or QueuedTaskStatus.Pending
            or QueuedTaskStatus.InProgress or QueuedTaskStatus.ServiceStopped;

    /// <summary>
    /// Category (ii) of the recovery filter: a recurring series that has nothing left to run but still
    /// carries a cursor, so it must be FINALIZED (Completed, cursor cleared) instead of executed.
    /// </summary>
    /// <remarks>
    /// Its pending slot falls at or past <see cref="RunUntil"/>, or the run budget is spent. Without this
    /// category such a row satisfies no predicate at all and stays <c>Queued</c> for ever, and no restart
    /// clears it. <see cref="QueuedTaskStatus.Cancelled"/> is excluded: a cancelled series is already
    /// terminal and must never be rewritten to Completed. The other terminal shapes (a Completed series
    /// with a null cursor, a poisoned Failed one) are excluded by the <c>NextRunUtc != null</c> term.
    /// </remarks>
    public bool IsRecurringSeriesToFinalize() =>
        IsRecurring
        && NextRunUtc != null
        && Status != QueuedTaskStatus.Cancelled
        && ((RunUntil != null && NextRunUtc >= RunUntil)
            || (MaxRuns != null && (CurrentRunCount ?? 0) >= MaxRuns));

    /// <summary>
    /// Stamps the invariant shape of a MATERIALIZED OCCURRENCE onto this row: a fresh one-shot belonging to
    /// <paramref name="scheduleId"/>, at <paramref name="scheduleVersion"/>, with everything that only a
    /// schedule row may carry cleared.
    /// </summary>
    /// <remarks>
    /// An occurrence is a one-shot by contract: its identity is the slot it already carries, and the
    /// definition, the cursor, the bounds and the task key stay on the schedule row. The optimized providers
    /// spell this shape out in the INSERT column list of their procedure or writable CTE; the stores that
    /// insert the caller's entity as it stands apply it here, so <c>MaterializeOccurrence</c> persists the
    /// same row on every backend instead of one shape per provider. Everything a caller legitimately supplies
    /// — id, creation time, slot, type, payload, handler, queue, audit level, runtime info — is preserved.
    /// </remarks>
    /// <param name="scheduleId">The schedule row that owns this occurrence.</param>
    /// <param name="scheduleVersion">The schedule version the occurrence was materialized against.</param>
    public void ApplyOccurrenceContract(Guid scheduleId, int scheduleVersion)
    {
        ParentTaskId    = scheduleId;
        ScheduleVersion = scheduleVersion;
        Status          = QueuedTaskStatus.WaitingQueue;
        CurrentRunCount = 0;
        IsRecurring     = false;

        // A schedule's own identity and definition never travel to a child: the task key is unique per row,
        // and a cursor or bounds on an occurrence would make it look like a series to the recovery filter.
        TaskKey       = null;
        RecurringTask = null;
        RecurringInfo = null;
        MaxRuns       = null;
        RunUntil      = null;
        NextRunUtc    = null;

        // Nothing has run yet.
        Exception                    = null;
        LastExecutionUtc             = null;
        ExecutionTimeMs              = 0;
        RecoveryDispatchFailureCount = null;
    }

    /// <summary>
    /// Rewrites every timestamp on this row to the SAME INSTANT at offset zero, so what a store persists
    /// never depends on the offset the caller happened to be carrying.
    /// </summary>
    /// <remarks>
    /// Applied by every storage at the public write entry points, <c>Persist</c> and <c>UpdateTask</c>.
    /// SQLite keeps a <see cref="DateTimeOffset"/> as ISO-8601 TEXT with the offset written inside it, so
    /// equality and ordering there are REPRESENTATIONAL: <c>10:00+02:00</c> and <c>08:00+00:00</c> are one
    /// instant and two different strings. Every cursor compare-and-swap normalizes its own operands, so the
    /// stored value is the one side of the comparison still free to disagree, and a row written with a
    /// non-zero offset loses all of them for ever. On the providers that compare instants this changes
    /// nothing but the offset a round-trip reports.
    /// </remarks>
    public void NormalizeTimestampsToUtc()
    {
        CreatedAtUtc          = CreatedAtUtc.ToUniversalTime();
        LastExecutionUtc      = LastExecutionUtc?.ToUniversalTime();
        ScheduledExecutionUtc = ScheduledExecutionUtc?.ToUniversalTime();
        RunUntil              = RunUntil?.ToUniversalTime();
        NextRunUtc            = NextRunUtc?.ToUniversalTime();
    }
}

public class StatusAudit
{
    public long             Id           { get; set; }
    public Guid             QueuedTaskId { get; set; }
    public DateTimeOffset   UpdatedAtUtc { get; set; }
    public QueuedTaskStatus NewStatus    { get; set; }
    public string?          Exception    { get; set; }

    public QueuedTask QueuedTask { get; set; } = null!;
}

public class RunsAudit
{
    public long             Id             { get; set; }
    public Guid             QueuedTaskId   { get; set; }
    public DateTimeOffset   ExecutedAt     { get; set; }
    public double           ExecutionTimeMs { get; set; }
    public QueuedTaskStatus Status         { get; set; }
    public string?          Exception      { get; set; }

    public QueuedTask QueuedTask { get; set; } = null!;
}

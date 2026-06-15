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
    /// Number of consecutive failed startup-recovery re-dispatch attempts (L18). Incremented each time
    /// recovery fails to re-dispatch this task; once it reaches the configured limit the task is
    /// poisoned (marked <see cref="QueuedTaskStatus.Failed"/>) so a persistent failure stops being
    /// retried at every restart. Reset after a successful re-dispatch. Not part of the recoverable
    /// predicate.
    /// </summary>
    public int?            RecoveryDispatchFailureCount { get; set; }

    public QueuedTaskStatus         Status       { get; set; }
    public ICollection<StatusAudit> StatusAudits { get; set; } = new List<StatusAudit>();
    public ICollection<RunsAudit>   RunsAudits   { get; set; } = new List<RunsAudit>();

    /// <summary>
    /// Collection of execution logs captured during task execution.
    /// Only populated if log capture is enabled in configuration.
    /// </summary>
    public ICollection<TaskExecutionLog> ExecutionLogs { get; set; } = new List<TaskExecutionLog>();

    /// <summary>
    /// Canonical recoverable predicate (client-side) shared by every storage provider: a task is
    /// recoverable on restart, and may be re-queued by <c>TrySetQueuedIfRecoverable</c>, only while
    /// it has runs left (<see cref="MaxRuns"/>), is not past its <see cref="RunUntil"/>, and sits in
    /// a non-terminal status (or is a recurring task between two runs).
    /// <para>
    /// THE single source of truth for the client-side evaluation. The EF Core server-side queries
    /// (<c>RetrievePending</c> / <c>TrySetQueuedIfRecoverable</c>) mirror this as a translatable
    /// LINQ expression — keep them in sync with this method.
    /// </para>
    /// </summary>
    public bool IsRecoverable(DateTimeOffset now) =>
        // < MaxRuns (not <=): a series at CurrentRunCount == MaxRuns is exhausted and terminal, matching
        // CalculateNextRun's `currentRun >= MaxRuns` (CU11/L27). null CurrentRunCount counts as 0 (L34).
        (MaxRuns == null || (CurrentRunCount ?? 0) < MaxRuns)
        && (RunUntil == null || RunUntil >= now)
        && (Status is QueuedTaskStatus.WaitingQueue or QueuedTaskStatus.Queued
                or QueuedTaskStatus.Pending or QueuedTaskStatus.ServiceStopped
                or QueuedTaskStatus.InProgress
            || (IsRecurring && NextRunUtc != null &&
                Status is QueuedTaskStatus.Completed or QueuedTaskStatus.Failed));
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

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
        (MaxRuns == null || CurrentRunCount <= MaxRuns)
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

namespace EverTask.Abstractions;

/// <summary>
/// Defines the level of audit trail persistence for task execution.
/// Controls what data is written to StatusAudit and RunsAudit tables.
/// </summary>
public enum AuditLevel
{
    /// <summary>
    /// Full audit trail (default behavior).
    /// Records all status transitions (Queued → InProgress → Completed/Failed)
    /// and all execution runs to StatusAudit and RunsAudit tables.
    /// Use for critical tasks requiring complete historical visibility.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Minimal audit trail - optimized for high-frequency recurring tasks.
    /// Only records errors and updates last execution timestamp in QueuedTask.
    /// Success executions: No StatusAudit/RunsAudit records, only QueuedTask.LastExecutionUtc updated.
    /// Failed executions: Full audit trail with exception details.
    /// Use for recurring tasks where only error visibility and last run tracking is needed.
    /// </summary>
    Minimal = 1,

    /// <summary>
    /// Errors-only audit trail.
    /// Only records failed executions to StatusAudit/RunsAudit tables.
    /// Success executions: No audit records, QueuedTask status updated to Completed.
    /// Failed executions: Full audit trail with exception details.
    /// Use when you only need to track failures, not execution frequency.
    /// </summary>
    ErrorsOnly = 2,

    /// <summary>
    /// No audit trail.
    /// Only updates QueuedTask table (status, last execution, exception).
    /// No records written to StatusAudit or RunsAudit tables.
    /// Use for extremely high-frequency tasks where audit data is not needed.
    /// Warning: No historical data will be available for debugging.
    /// </summary>
    None = 3
}

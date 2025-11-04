namespace EverTask;

/// <summary>
/// Defines retention policies for audit trail data.
/// Controls how long audit records are retained in StatusAudit and RunsAudit tables.
/// Retention is enforced by the optional <see cref="AuditCleanupHostedService"/>.
/// </summary>
public sealed class AuditRetentionPolicy
{
    /// <summary>
    /// Gets or sets the number of days to retain status audit records.
    /// Applies to both successful and failed status transitions in the StatusAudit table.
    /// Set to null for unlimited retention (default).
    /// </summary>
    /// <remarks>
    /// Status audit records older than this threshold will be deleted by the cleanup service.
    /// Example: Setting to 30 means only the last 30 days of status changes are kept.
    /// </remarks>
    public int? StatusAuditRetentionDays { get; set; }

    /// <summary>
    /// Gets or sets the number of days to retain execution audit records.
    /// Applies to recurring task execution history in the RunsAudit table.
    /// Set to null for unlimited retention (default).
    /// </summary>
    /// <remarks>
    /// Runs audit records older than this threshold will be deleted by the cleanup service.
    /// Example: Setting to 7 means only the last 7 days of execution history are kept.
    /// </remarks>
    public int? RunsAuditRetentionDays { get; set; }

    /// <summary>
    /// Gets or sets the number of days to retain error audit records.
    /// When set, overrides <see cref="StatusAuditRetentionDays"/> and <see cref="RunsAuditRetentionDays"/>
    /// for records containing exceptions, allowing longer retention of failures.
    /// Set to null to use the same retention as successful executions (default).
    /// </summary>
    /// <remarks>
    /// Useful for keeping error history longer than successful executions.
    /// Example: StatusAuditRetentionDays=7, ErrorAuditRetentionDays=90 keeps
    /// successful executions for 7 days but errors for 90 days.
    /// </remarks>
    public int? ErrorAuditRetentionDays { get; set; }

    /// <summary>
    /// Gets or sets whether to delete completed tasks when their audit trail is deleted.
    /// When true, QueuedTask records with status Completed will be deleted when all
    /// their audit records are purged (useful for preventing unbounded growth).
    /// When false, QueuedTask records are preserved indefinitely (default).
    /// </summary>
    /// <remarks>
    /// Applies only to non-recurring tasks with status Completed.
    /// Recurring tasks are never auto-deleted as they need to be rescheduled.
    /// Failed/Cancelled tasks are also preserved for visibility.
    /// </remarks>
    public bool DeleteCompletedTasksWithAudits { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditRetentionPolicy"/> class
    /// with default settings (unlimited retention).
    /// </summary>
    public AuditRetentionPolicy()
    {
        StatusAuditRetentionDays = null;
        RunsAuditRetentionDays = null;
        ErrorAuditRetentionDays = null;
        DeleteCompletedTasksWithAudits = false;
    }

    /// <summary>
    /// Creates a retention policy with the specified number of days for all audit types.
    /// </summary>
    /// <param name="retentionDays">Number of days to retain all audit records.</param>
    /// <returns>A new <see cref="AuditRetentionPolicy"/> instance.</returns>
    public static AuditRetentionPolicy WithUniformRetention(int retentionDays)
    {
        return new AuditRetentionPolicy
        {
            StatusAuditRetentionDays = retentionDays,
            RunsAuditRetentionDays = retentionDays,
            ErrorAuditRetentionDays = retentionDays
        };
    }

    /// <summary>
    /// Creates a retention policy that keeps errors longer than successful executions.
    /// </summary>
    /// <param name="successRetentionDays">Number of days to retain successful execution audits.</param>
    /// <param name="errorRetentionDays">Number of days to retain error audits.</param>
    /// <returns>A new <see cref="AuditRetentionPolicy"/> instance.</returns>
    public static AuditRetentionPolicy WithErrorPriority(int successRetentionDays, int errorRetentionDays)
    {
        return new AuditRetentionPolicy
        {
            StatusAuditRetentionDays = successRetentionDays,
            RunsAuditRetentionDays = successRetentionDays,
            ErrorAuditRetentionDays = errorRetentionDays
        };
    }
}

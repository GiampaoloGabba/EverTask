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
    /// Gets or sets whether completed, non-recurring tasks are hard-deleted once their audit trail
    /// has aged out. Disabled by default — task rows are preserved indefinitely.
    /// </summary>
    /// <remarks>
    /// When true, a <c>Completed</c> non-recurring task is deleted only when ALL of the following hold:
    /// <list type="bullet">
    /// <item>it is older than the longest configured retention window — the maximum of
    /// <see cref="StatusAuditRetentionDays"/>, <see cref="RunsAuditRetentionDays"/> and
    /// <see cref="ErrorAuditRetentionDays"/> (measured against <c>LastExecutionUtc</c>, falling back to
    /// <c>CreatedAtUtc</c>);</item>
    /// <item>it has no remaining StatusAudit or RunsAudit rows;</item>
    /// <item>it has no captured execution logs (those are cascade-deleted with the task and have no
    /// retention of their own).</item>
    /// </list>
    /// If no audit retention window is configured, no completed tasks are deleted (there is no age cutoff).
    /// Recurring tasks are never auto-deleted as they need to be rescheduled; Failed/Cancelled tasks are
    /// preserved for visibility.
    /// </remarks>
    public bool DeleteCompletedTasksAfterRetention { get; set; }

    /// <summary>
    /// Deprecated alias for <see cref="DeleteCompletedTasksAfterRetention"/>. The original name and its
    /// old behavior were misleading: it deleted completed tasks that had <em>no</em> audit rows
    /// immediately, with no age cutoff and ignoring captured execution logs. It now forwards to
    /// <see cref="DeleteCompletedTasksAfterRetention"/>, which enforces the retention-age gate and
    /// preserves tasks that still have logs.
    /// </summary>
    [Obsolete("Renamed to DeleteCompletedTasksAfterRetention (which now also enforces an age cutoff and " +
              "preserves tasks that still have execution logs). This alias forwards to it and will be " +
              "removed in a future major version.")]
    public bool DeleteCompletedTasksWithAudits
    {
        get => DeleteCompletedTasksAfterRetention;
        set => DeleteCompletedTasksAfterRetention = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditRetentionPolicy"/> class
    /// with default settings (unlimited retention).
    /// </summary>
    public AuditRetentionPolicy()
    {
        StatusAuditRetentionDays = null;
        RunsAuditRetentionDays = null;
        ErrorAuditRetentionDays = null;
        DeleteCompletedTasksAfterRetention = false;
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

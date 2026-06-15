namespace EverTask;

/// <summary>
/// Defines retention policies for audit and diagnostic data.
/// Controls how long records are retained in the StatusAudit, RunsAudit and TaskExecutionLog tables.
/// Retention is enforced by the optional <see cref="AuditCleanupHostedService"/>.
/// </summary>
/// <remarks>
/// Every day/count knob below uses the same convention: <c>null</c> means "unlimited / disabled" and any
/// value <c>&lt;= 0</c> is also treated as <b>disabled</b> (a no-op, logged as a warning), never as a
/// "now"/future cutoff. This keeps a typo or a missing <c>IConfiguration</c> binding (an absent env var
/// binds to 0) from silently turning a cleanup cycle into a mass deletion.
/// </remarks>
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
    /// Gets or sets the number of days to retain captured execution logs (the <c>TaskExecutionLog</c>
    /// rows written when persistent logging is enabled). Set to null for unlimited retention (default).
    /// </summary>
    /// <remarks>
    /// Execution logs older than this threshold (anchored on <c>TimestampUtc</c>) are deleted by the
    /// cleanup service, <em>independently</em> of their parent task — so a long-running service, and
    /// recurring tasks in particular, never accumulate logs without bound. This is distinct from the
    /// per-execution cap (<c>PersistentLoggerOptions.MaxLogsPerTask</c>), which limits how many logs a
    /// <em>single</em> run may persist; this window trims old logs across <em>all</em> past runs.
    /// Default null keeps current behavior: enabling persistent logging never silently starts deleting
    /// logs.
    /// </remarks>
    public int? ExecutionLogRetentionDays { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of execution logs to keep per task across all of its runs.
    /// When a task has more than this many <c>TaskExecutionLog</c> rows, the cleanup service deletes the
    /// oldest ones (by <c>TimestampUtc</c>, then <c>SequenceNumber</c>) until only the most recent
    /// <c>MaxExecutionLogsPerTask</c> remain. Set to null to disable the count cap (default).
    /// </summary>
    /// <remarks>
    /// This is a per-task, cross-run cap that bounds total log growth even when no time-based window is
    /// set — useful for high-frequency recurring tasks where <see cref="ExecutionLogRetentionDays"/>
    /// alone would still allow a very large row count. It is unrelated to
    /// <c>PersistentLoggerOptions.MaxLogsPerTask</c>, which caps a single execution's logs at capture
    /// time. When both this and <see cref="ExecutionLogRetentionDays"/> are set, a log is deleted if it
    /// violates either rule.
    /// </remarks>
    public int? MaxExecutionLogsPerTask { get; set; }

    /// <summary>
    /// Gets or sets whether completed, non-recurring tasks are hard-deleted once their audit trail
    /// has aged out. Disabled by default — task rows are preserved indefinitely.
    /// </summary>
    /// <remarks>
    /// When true, a <c>Completed</c> non-recurring task is deleted once it is older than the longest
    /// configured retention window — the maximum of <see cref="StatusAuditRetentionDays"/>,
    /// <see cref="RunsAuditRetentionDays"/> and <see cref="ErrorAuditRetentionDays"/> (measured against
    /// <c>LastExecutionUtc</c>, falling back to <c>CreatedAtUtc</c>) — and has no remaining StatusAudit or
    /// RunsAudit rows. When a log retention window or cap (<see cref="ExecutionLogRetentionDays"/> /
    /// <see cref="MaxExecutionLogsPerTask"/>) is active, a task that still owns execution logs — logs the
    /// log-retention passes deliberately kept — is also preserved, so a purge only cascade-deletes logs once
    /// they have aged out on their own. With no log retention configured, deleting the task cascades to
    /// everything it owns, execution logs included.
    /// If no audit retention window is configured, no completed tasks are deleted (there is no age cutoff);
    /// a non-positive window counts as disabled and does not contribute a cutoff.
    /// Recurring tasks are never auto-deleted as they need to be rescheduled; Failed/Cancelled tasks are
    /// preserved for visibility. Execution logs can also be trimmed on their own — independently of the
    /// task — via <see cref="ExecutionLogRetentionDays"/> and <see cref="MaxExecutionLogsPerTask"/>.
    /// </remarks>
    public bool DeleteCompletedTasksAfterRetention { get; set; }

    /// <summary>
    /// Deprecated alias for <see cref="DeleteCompletedTasksAfterRetention"/>. The original name and its
    /// old behavior were misleading: it deleted completed tasks that had <em>no</em> audit rows
    /// immediately, with no age cutoff and ignoring captured execution logs. It now forwards to
    /// <see cref="DeleteCompletedTasksAfterRetention"/>, which enforces the retention-age gate and — when a
    /// log retention window or cap is active — preserves tasks that still have logs kept by it.
    /// </summary>
    [Obsolete("Renamed to DeleteCompletedTasksAfterRetention (which now also enforces an age cutoff and, " +
              "when a log retention is active, preserves tasks that still have execution logs). This alias " +
              "forwards to it and will be removed in a future major version.")]
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
        ExecutionLogRetentionDays = null;
        MaxExecutionLogsPerTask = null;
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

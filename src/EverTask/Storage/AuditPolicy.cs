using EverTask.Abstractions;

namespace EverTask.Storage;

/// <summary>
/// Single source of truth for audit-trail creation decisions, shared by every <see cref="ITaskStorage"/>
/// provider (MemoryTaskStorage, the EF Core base and its derivatives). Keeping the rules here means the
/// audit trail for the same event is identical regardless of the storage backend — the relational
/// providers and the in-memory provider can never drift (F19/L29).
/// </summary>
public static class AuditPolicy
{
    /// <summary>
    /// Whether a status/exception pair represents a real error worth auditing. Excludes expected
    /// shutdown: a <see cref="QueuedTaskStatus.ServiceStopped"/> carrying an
    /// <see cref="OperationCanceledException"/> is the host stopping, not a failure.
    /// </summary>
    public static bool IsRealError(QueuedTaskStatus status, Exception? exception) =>
        status switch
        {
            QueuedTaskStatus.Failed => true,
            QueuedTaskStatus.ServiceStopped when exception is OperationCanceledException => false,
            _ => exception != null
        };

    /// <summary>
    /// Whether a status transition should produce a <see cref="StatusAudit"/> row at the given level.
    /// </summary>
    public static bool ShouldCreateStatusAudit(AuditLevel auditLevel, QueuedTaskStatus status, Exception? exception) =>
        auditLevel switch
        {
            AuditLevel.None => false,
            AuditLevel.ErrorsOnly or AuditLevel.Minimal => IsRealError(status, exception),
            AuditLevel.Full => true,
            _ => true // Default to full audit for unknown levels
        };

    /// <summary>
    /// Whether a recurring run should produce a <see cref="RunsAudit"/> row at the given level.
    /// </summary>
    public static bool ShouldCreateRunsAudit(AuditLevel auditLevel, QueuedTaskStatus status, string? exception) =>
        auditLevel switch
        {
            AuditLevel.None => false,
            AuditLevel.ErrorsOnly => !string.IsNullOrEmpty(exception) || status == QueuedTaskStatus.Failed,
            AuditLevel.Minimal => true, // Minimal still tracks the last run for recurring tasks
            AuditLevel.Full => true,
            _ => true // Default to full audit for unknown levels
        };
}

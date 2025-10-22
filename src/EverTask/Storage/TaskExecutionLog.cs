namespace EverTask.Storage;

/// <summary>
/// Represents a single log entry captured during task execution.
/// </summary>
public class TaskExecutionLog
{
    /// <summary>
    /// Unique identifier for this log entry.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the parent <see cref="QueuedTask"/>.
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// Timestamp when the log was written (UTC).
    /// </summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>
    /// Log level as string: "Trace", "Debug", "Information", "Warning", "Error", "Critical".
    /// </summary>
    public string Level { get; set; } = null!;

    /// <summary>
    /// The log message.
    /// </summary>
    public string Message { get; set; } = null!;

    /// <summary>
    /// Exception details if an exception was logged (optional).
    /// </summary>
    public string? ExceptionDetails { get; set; }

    /// <summary>
    /// Sequence number to preserve log order within the same task execution.
    /// Incremented for each log entry (0, 1, 2, ...).
    /// </summary>
    public int SequenceNumber { get; set; }

    /// <summary>
    /// Navigation property to the parent task.
    /// </summary>
    public QueuedTask Task { get; set; } = null!;
}

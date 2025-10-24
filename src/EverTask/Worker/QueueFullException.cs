namespace EverTask.Worker;

/// <summary>
/// Exception thrown when a queue is full and the QueueFullBehavior is set to ThrowException.
/// </summary>
public class QueueFullException : Exception
{
    public string QueueName { get; }
    public Guid TaskId { get; }

    public QueueFullException(string queueName, Guid taskId)
        : base($"Queue '{queueName}' is full and cannot accept task {taskId}")
    {
        QueueName = queueName;
        TaskId = taskId;
    }

    public QueueFullException(string queueName, Guid taskId, string message)
        : base(message)
    {
        QueueName = queueName;
        TaskId = taskId;
    }

    public QueueFullException(string queueName, Guid taskId, string message, Exception innerException)
        : base(message, innerException)
    {
        QueueName = queueName;
        TaskId = taskId;
    }
}

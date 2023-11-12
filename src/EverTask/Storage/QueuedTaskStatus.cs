namespace EverTask.Storage;

public enum QueuedTaskStatus
{
    WaitingQueue,
    Queued,
    InProgress,
    Pending,
    Cancelled,
    Completed,
    Failed
}

namespace EverTask.Logging;

/// <summary>
/// Internal extension of ITaskLogCapture that provides access to persisted logs.
/// Used by WorkerExecutor to retrieve and save logs after task execution.
/// </summary>
internal interface ITaskLogCaptureInternal : ITaskLogCapture
{
    /// <summary>
    /// Gets all persisted logs. Used internally by WorkerExecutor.
    /// Returns empty list if persistence is disabled.
    /// </summary>
    /// <returns>Read-only list of persisted logs ordered by sequence number.</returns>
    IReadOnlyList<TaskExecutionLog> GetPersistedLogs();
}

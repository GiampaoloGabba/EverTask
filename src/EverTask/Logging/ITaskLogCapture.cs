namespace EverTask.Logging;

/// <summary>
/// Internal extension of ITaskLogCapture that provides access to captured logs.
/// Used by WorkerExecutor to retrieve and save logs after task execution.
/// </summary>
internal interface ITaskLogCaptureInternal : ITaskLogCapture
{
    /// <summary>
    /// Gets all captured logs. Used internally by WorkerExecutor.
    /// </summary>
    /// <returns>Read-only list of captured logs ordered by sequence number.</returns>
    IReadOnlyList<TaskExecutionLog> GetCapturedLogs();
}

namespace EverTask.Storage;

/// <summary>
/// Extension methods for <see cref="ITaskStorage"/> providing convenient access to execution logs.
/// </summary>
public static class TaskStorageExtensions
{
    /// <summary>
    /// Gets all execution logs for a specific task, ordered by sequence number.
    /// </summary>
    /// <param name="storage">The task storage instance.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read-only list of execution logs ordered by sequence number.</returns>
    /// <exception cref="ArgumentNullException">Thrown if storage is null.</exception>
    public static async Task<IReadOnlyList<TaskExecutionLog>> GetLogsAsync(
        this ITaskStorage storage,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        if (storage == null)
            throw new ArgumentNullException(nameof(storage));

        // GetExecutionLogsAsync already returns logs ordered by SequenceNumber
        return await storage.GetExecutionLogsAsync(taskId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a paginated list of execution logs for a specific task, ordered by sequence number.
    /// </summary>
    /// <param name="storage">The task storage instance.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="pageNumber">Page number (1-based). Must be >= 1.</param>
    /// <param name="pageSize">Number of logs per page. Must be >= 1 and <= 1000.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read-only list of execution logs for the requested page.</returns>
    /// <exception cref="ArgumentNullException">Thrown if storage is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if pageNumber < 1, pageSize < 1, or pageSize > 1000.</exception>
    public static async Task<IReadOnlyList<TaskExecutionLog>> GetLogsAsync(
        this ITaskStorage storage,
        Guid taskId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (storage == null)
            throw new ArgumentNullException(nameof(storage));

        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be >= 1.");

        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be >= 1.");

        if (pageSize > 1000)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be <= 1000 to prevent excessive memory usage.");

        // Use the storage method with skip/take to perform pagination at the database level
        var skip = (pageNumber - 1) * pageSize;
        return await storage.GetExecutionLogsAsync(taskId, skip, pageSize, cancellationToken).ConfigureAwait(false);
    }
}

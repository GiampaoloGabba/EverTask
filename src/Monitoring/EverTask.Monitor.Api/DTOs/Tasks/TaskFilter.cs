using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Query filters for task list.
/// </summary>
public class TaskFilter
{
    /// <summary>
    /// Filter by task status(es)
    /// </summary>
    public List<QueuedTaskStatus>? Statuses { get; set; }

    /// <summary>
    /// Filter by task type (contains filter)
    /// </summary>
    public string? TaskType { get; set; }

    /// <summary>
    /// Filter by queue name
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>
    /// Filter by recurring flag
    /// </summary>
    public bool? IsRecurring { get; set; }

    /// <summary>
    /// Filter tasks created after this date
    /// </summary>
    public DateTimeOffset? CreatedAfter { get; set; }

    /// <summary>
    /// Filter tasks created before this date
    /// </summary>
    public DateTimeOffset? CreatedBefore { get; set; }

    /// <summary>
    /// Search term (searches in Type, Handler, TaskKey)
    /// </summary>
    public string? SearchTerm { get; set; }
}

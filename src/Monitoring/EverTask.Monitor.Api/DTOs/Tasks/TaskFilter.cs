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

    /// <summary>
    /// Keep only the occurrences of this durable schedule.
    /// </summary>
    public Guid? ParentTaskId { get; set; }

    /// <summary>
    /// When true, keep only materialized occurrences; when false, keep only rows that are not occurrences.
    /// </summary>
    public bool? OnlyOccurrences { get; set; }

    /// <summary>
    /// When true, keep only the occurrences that stand for missed work — a replayed slot or a run of missed
    /// slots collapsed into one. When false, keep only the rows that stand for none.
    /// </summary>
    public bool? OnlyCatchUp { get; set; }
}

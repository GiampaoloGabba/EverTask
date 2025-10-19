namespace EverTask.Configuration;

/// <summary>
/// Well-known queue names used by EverTask.
/// </summary>
public static class QueueNames
{
    /// <summary>
    /// The default queue for tasks without an explicit queue name.
    /// This queue is always created automatically.
    /// </summary>
    public const string Default = "default";

    /// <summary>
    /// The queue for recurring tasks that don't specify an explicit queue name.
    /// This queue is created automatically if not explicitly configured.
    /// </summary>
    public const string Recurring = "recurring";
}

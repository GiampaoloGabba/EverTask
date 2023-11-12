namespace EverTask.Storage;

public class QueuedTask
{
    public Guid            Id               { get; set; }
    public DateTimeOffset  CreatedAtUtc     { get; set; }
    public DateTimeOffset? LastExecutionUtc { get; set; }
    public string          Type             { get; set; } = "";
    public string          Request          { get; set; } = "";
    public string          Handler          { get; set; } = "";
    public string?         Exception        { get; set; }

    public QueuedTaskStatus         Status       { get; set; }
    public ICollection<StatusAudit> StatusAudits { get; set; } = Array.Empty<StatusAudit>();
}

public class StatusAudit
{
    public long             Id           { get; set; }
    public Guid             QueuedTaskId { get; set; }
    public DateTimeOffset   UpdatedAtUtc { get; set; }
    public QueuedTaskStatus NewStatus    { get; set; }
    public string?          Exception    { get; set; }

    public QueuedTask QueuedTask { get; set; } = null!;
}

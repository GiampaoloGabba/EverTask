namespace EverTask.Storage;

public class QueuedTask
{
    public Guid            Id                    { get; set; }
    public DateTimeOffset  CreatedAtUtc          { get; set; }
    public DateTimeOffset? LastExecutionUtc      { get; set; }
    public DateTimeOffset? ScheduledExecutionUtc { get; set; }
    public string          Type                  { get; set; } = "";
    public string          Request               { get; set; } = "";
    public string          Handler               { get; set; } = "";
    public string?         Exception             { get; set; }
    public bool            IsRecurring           { get; set; }
    public string?         RecurringTask         { get; set; }
    public string?         RecurringInfo         { get; set; }
    public int?            CurrentRunCount       { get; set; }
    public int?            MaxRuns               { get; set; }
    public DateTimeOffset? NextRunUtc            { get; set; }

    public QueuedTaskStatus         Status       { get; set; }
    public ICollection<StatusAudit> StatusAudits { get; set; } = new List<StatusAudit>();
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

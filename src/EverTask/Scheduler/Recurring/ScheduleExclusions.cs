using System.Text.Json.Serialization;

namespace EverTask.Scheduler.Recurring;

/// <summary>The fixed days, dates and absolute windows removed from a recurring schedule's grid.</summary>
public sealed class ScheduleExclusions
{
    /// <summary>Public and parameterless for the persisted schedule serializer.</summary>
    [JsonConstructor]
    public ScheduleExclusions() { }

    /// <summary>Whole days of the week, read on the schedule's exclusion clock.</summary>
    public DayOfWeek[] Days { get; set; } = [];

    /// <summary>Whole calendar dates, read on the schedule's exclusion clock.</summary>
    public DateOnly[] Dates { get; set; } = [];

    /// <summary>Absolute half-open windows.</summary>
    public ExclusionRange[] Ranges { get; set; } = [];
}

/// <summary>An absolute half-open exclusion window <c>[FromUtc, ToUtc)</c>.</summary>
public sealed class ExclusionRange
{
    /// <summary>Public and parameterless for the persisted schedule serializer.</summary>
    [JsonConstructor]
    public ExclusionRange() { }

    /// <summary>The inclusive start, normalized to offset zero by schedule validation.</summary>
    public DateTimeOffset FromUtc { get; set; }

    /// <summary>The exclusive end, normalized to offset zero by schedule validation.</summary>
    public DateTimeOffset ToUtc { get; set; }
}

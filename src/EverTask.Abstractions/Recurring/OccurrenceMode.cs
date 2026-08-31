namespace EverTask.Abstractions;

/// <summary>
/// How a recurring schedule produces its occurrences.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Inline"/> is the historical behaviour and stays the default: the schedule row itself runs the
/// handler and re-parks its next occurrence, so an occurrence has no durable identity of its own.
/// </para>
/// <para>
/// <see cref="Durable"/> turns the schedule row into a definition plus a cursor: each due slot becomes its own
/// one-shot child row (a real <c>QueuedTask</c> with its own status, retries and audit). Only the durable mode
/// can carry misfire policies that replay missed slots.
/// </para>
/// </remarks>
public enum OccurrenceMode
{
    /// <summary>The schedule row executes the handler itself (default, legacy behaviour).</summary>
    Inline = 0,

    /// <summary>Every due slot is materialized as its own durable child row.</summary>
    Durable = 1
}

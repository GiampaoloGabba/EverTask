namespace EverTask.Abstractions;

/// <summary>
/// The identity of the delivery a handler is running right now: which row it is, which schedule it belongs to,
/// the slot it stands for and how many times it has already been attempted.
/// </summary>
/// <remarks>
/// <para>
/// Two ways to reach it: <see cref="EverTaskHandler{TTask}.Context"/> inside a handler that derives from the
/// base class, and <see cref="ITaskExecutionContextAccessor"/> for anything else in the dependency graph.
/// The instance is created per delivery and stays valid for <c>Handle</c> and every lifecycle callback.
/// </para>
/// <para>
/// Everything here is read-only for the handler, but <see cref="Attempt"/> changes across retries: read it
/// when you need it rather than caching it at the start of <c>Handle</c>.
/// </para>
/// </remarks>
public interface ITaskExecutionContext
{
    /// <summary>The persistence id of the row being executed. For an occurrence, the occurrence's own id.</summary>
    Guid TaskId { get; }

    /// <summary>The recurring schedule this delivery is an occurrence of, or null when it is not one.</summary>
    Guid? ScheduleId { get; }

    /// <summary>The idempotency key the task was dispatched with, when it had one.</summary>
    string? TaskKey { get; }

    /// <summary>
    /// The nominal slot this delivery stands for: the scheduled time of a delayed task, the occurrence time of
    /// a recurring one, null for a task dispatched to run immediately. Never the slot a rate-limit deferral
    /// re-parked the task at — that one moves, this one does not.
    /// </summary>
    DateTimeOffset? ScheduledAtUtc { get; }

    /// <summary>
    /// <see cref="ScheduledAtUtc"/> in the schedule's own time zone, offset included so the two passes of a
    /// DST fall-back are distinguishable. Null when the schedule carries no zone (the legacy UTC behavior).
    /// </summary>
    DateTimeOffset? ScheduledAtLocal { get; }

    /// <summary>The IANA id of the schedule's time zone, or null when it has none.</summary>
    string? TimeZoneId { get; }

    /// <summary>When this delivery actually started, on the scheduling clock.</summary>
    DateTimeOffset StartedAtUtc { get; }

    /// <summary>
    /// The 1-based execution attempt: 1 on the first run of <c>Handle</c>, 2 on the first retry. Inside
    /// <c>OnRetry</c> it is the attempt that is about to start; inside <c>OnError</c>, the last one that ran.
    /// </summary>
    int Attempt { get; }

    /// <summary>
    /// The 1-based run number within a recurring series (1 for a one-shot task). It survives a restart: it
    /// comes from the durable run counter, not from a process-local count.
    /// </summary>
    int RunNumber { get; }

    /// <summary>The version of the schedule definition behind this delivery. 0 for a schedule never rescheduled.</summary>
    int ScheduleVersion { get; }

    /// <summary>True when this delivery belongs to a recurring series, either as a run of it or as one of its occurrences.</summary>
    bool IsRecurring { get; }

    /// <summary>True when this delivery is a durable occurrence: a row of its own, owned by a schedule row.</summary>
    bool IsOccurrence { get; }

    /// <summary>How late the delivery is, when it is late at all. Null for a delivery that ran on time.</summary>
    MisfireInfo? Misfire { get; }
}

namespace EverTask.Monitor.Api.DTOs.Management;

/// <summary>
/// The outcome of a management operation — requeue, resume or cancel — as both the API and its caller see it.
/// </summary>
/// <param name="Status">How the operation ended; the controller maps it to the HTTP status.</param>
/// <param name="Message">What happened, in the words an operator reading the dashboard needs.</param>
public record ManagementActionDto(
    ManagementActionStatus Status,
    string Message
)
{
    /// <summary>The row the operation was asked about, when one was found.</summary>
    public Guid? TaskId { get; init; }

    /// <summary>
    /// Where the schedule stands after the call: the slot it will fire next, or absent when the operation
    /// left no cursor (a cancel, a requeue, anything refused).
    /// </summary>
    public DateTimeOffset? NextRunUtc { get; init; }

    /// <summary>
    /// Whether the call cleared a standing catch-up halt. Only a resume can, and only when one was standing.
    /// </summary>
    public bool ReleasedHalt { get; init; }
}

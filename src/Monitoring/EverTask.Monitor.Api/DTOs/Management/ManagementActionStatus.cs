namespace EverTask.Monitor.Api.DTOs.Management;

/// <summary>
/// How a management operation ended. It is what the controller maps to an HTTP status, so the two can never
/// tell a different story.
/// </summary>
public enum ManagementActionStatus
{
    /// <summary>The operation was applied (200).</summary>
    Succeeded,

    /// <summary>No task carries that id (404).</summary>
    NotFound,

    /// <summary>
    /// The row is not in a state the operation applies to (409): an occurrence that is not terminal, a
    /// one-shot asked to resume, a schedule that was already cancelled, a schedule dispatched without a key.
    /// </summary>
    Conflict,

    /// <summary>
    /// The host cannot perform the operation at all (501): no schedule manager is registered, or the
    /// registered storage does not implement the capability the operation needs.
    /// </summary>
    NotSupported,

    /// <summary>
    /// A dependency the operation had to ask could not answer, and nothing was written (503). The schedule's
    /// occurrence provider is the one such dependency; the failure is transient by contract, so the call can
    /// simply be made again.
    /// </summary>
    Unavailable
}

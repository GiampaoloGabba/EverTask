namespace EverTask.Monitor.Api.Services;

/// <summary>
/// The two roles a monitoring session can carry. The API is read-only by construction, so the split is
/// between looking at it and operating on it — nothing finer.
/// </summary>
public static class MonitoringRoles
{
    /// <summary>The claim type the role travels in, written verbatim (no inbound claim-type mapping).</summary>
    public const string ClaimType = "role";

    /// <summary>Everything the API answers with a GET. What the dashboard credential grants.</summary>
    public const string Read = "read";

    /// <summary>
    /// The read surface plus the management endpoints. Granted only by the second credential
    /// (<c>ManagementUsername</c>/<c>ManagementPassword</c>), never by the dashboard one and never by a
    /// magic link.
    /// </summary>
    public const string Operate = "operate";
}

namespace EverTask.Storage;

/// <summary>
/// Outcome of an advance guarded by a compare-and-swap on the schedule version.
/// </summary>
public enum ScheduleCasResult
{
    /// <summary>The write was applied: the row still carried the expected schedule version.</summary>
    Applied = 0,

    /// <summary>
    /// Nothing was written. The row was rescheduled under the caller (or is gone), so the caller must
    /// re-read it and recompute against the current definition instead of forcing its stale one.
    /// </summary>
    VersionMismatch = 1
}

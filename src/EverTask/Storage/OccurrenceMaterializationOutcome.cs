namespace EverTask.Storage;

/// <summary>
/// Outcome of the atomic "insert the occurrence and advance the schedule cursor" operation.
/// </summary>
/// <remarks>
/// Everything but <see cref="Created"/> means the caller lost a race and must re-read the schedule row
/// before deciding again — no partial write ever happened.
/// </remarks>
public enum OccurrenceMaterializationOutcome
{
    /// <summary>The occurrence row was inserted and the cursor advanced in the same transaction.</summary>
    Created = 0,

    /// <summary>Another writer had already materialized this exact slot (unique constraint on parent + slot).</summary>
    AlreadyExists = 1,

    /// <summary>The schedule cursor no longer holds the expected value: someone else advanced it.</summary>
    CursorMoved = 2,

    /// <summary>The schedule definition was rescheduled: its version no longer matches the expected one.</summary>
    VersionMismatch = 3,

    /// <summary>The schedule row is gone or in a terminal status, so it must not grow new occurrences.</summary>
    ParentInactive = 4
}

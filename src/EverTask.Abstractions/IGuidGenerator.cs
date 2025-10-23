namespace EverTask;

/// <summary>
/// Provides database-optimized GUID generation.
/// Implementations can generate GUIDs optimized for specific database engines
/// to minimize index fragmentation and improve insert performance.
/// </summary>
public interface IGuidGenerator
{
    /// <summary>
    /// Generates a database-friendly GUID (UUID v7 or v8) that is temporally ordered
    /// and optimized for the target database's uniqueidentifier sorting algorithm.
    /// </summary>
    /// <returns>A new GUID optimized for database indexes.</returns>
    Guid NewDatabaseFriendly();
}

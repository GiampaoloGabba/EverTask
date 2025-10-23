using UUIDNext;

namespace EverTask;

/// <summary>
/// Default GUID generator that produces database-optimized UUIDs (v7 or v8)
/// based on the target database type.
/// </summary>
public sealed class DefaultGuidGenerator : IGuidGenerator
{
    private readonly Database _database;

    /// <summary>
    /// Creates a new GUID generator for the specified database type.
    /// </summary>
    /// <param name="database">Target database type (SqlServer, Sqlite, PostgreSql, Other).</param>
    public DefaultGuidGenerator(Database database)
    {
        _database = database;
    }

    /// <summary>
    /// Generates a database-friendly GUID optimized for the target database's
    /// uniqueidentifier/BLOB sorting algorithm.
    /// </summary>
    public Guid NewDatabaseFriendly()
    {
        return Uuid.NewDatabaseFriendly(_database);
    }
}

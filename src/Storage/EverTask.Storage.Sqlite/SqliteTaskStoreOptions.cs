namespace EverTask.Storage.Sqlite;

/// <summary>
/// Configuration options for SQLite task storage.
/// </summary>
public class SqliteTaskStoreOptions : ITaskStoreOptions
{
    /// <summary>
    /// Gets or sets whether to automatically apply pending migrations on startup.
    /// Default: true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When true, database schema updates are applied automatically when the application starts.
    /// This is especially important for in-memory SQLite databases, which require migration on every startup.
    /// </para>
    /// <para>
    /// For file-based SQLite databases in production, consider setting this to false
    /// and using deployment scripts to apply migrations.
    /// </para>
    /// </remarks>
    public bool    AutoApplyMigrations { get; set; } = true;

    /// <summary>
    /// Gets or sets the database schema name for EverTask tables.
    /// MUST be "" (empty string) for SQLite - SQLite does not support schemas.
    /// </summary>
    /// <remarks>
    /// SQLite does not support schemas like SQL Server.
    /// This property must remain an empty string. Do not change this value.
    /// </remarks>
    public string? SchemaName          { get; set; } = "";
}

namespace EverTask.Storage.EfCore;

/// <summary>
/// Configuration options for EF Core-based task storage providers.
/// </summary>
public interface ITaskStoreOptions
{
    /// <summary>
    /// Gets or sets whether to automatically apply pending migrations on startup.
    /// Default: true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When true, the database schema is automatically updated when the application starts.
    /// This is convenient for development and testing.
    /// </para>
    /// <para>
    /// For production deployments, consider setting this to false and applying migrations
    /// via deployment scripts or a database initializer to avoid startup delays and concurrency issues.
    /// </para>
    /// </remarks>
    public bool    AutoApplyMigrations { get; set; }

    /// <summary>
    /// Gets or sets the database schema name for EverTask tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQL Server default: "EverTask"
    /// SQLite: Must be "" (empty string) - SQLite does not support schemas
    /// </para>
    /// <para>
    /// This value is used during migrations to create schema-qualified table names.
    /// Changing this after initial migration requires manual database updates.
    /// </para>
    /// </remarks>
    public string? SchemaName          { get; set; }
}

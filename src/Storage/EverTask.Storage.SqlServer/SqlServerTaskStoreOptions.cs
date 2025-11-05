namespace EverTask.Storage.SqlServer;

/// <summary>
/// Configuration options for SQL Server task storage.
/// </summary>
public class SqlServerTaskStoreOptions : ITaskStoreOptions
{
    /// <summary>
    /// Gets or sets whether to automatically apply pending migrations on startup.
    /// Default: true.
    /// </summary>
    /// <remarks>
    /// When true, database schema updates are applied automatically when the application starts.
    /// For production, consider setting this to false and using deployment scripts to apply migrations.
    /// </remarks>
    public bool    AutoApplyMigrations { get; set; } = true;

    /// <summary>
    /// Gets or sets the database schema name for EverTask tables.
    /// Default: "EverTask".
    /// </summary>
    /// <remarks>
    /// <para>
    /// EverTask tables (QueuedTasks, StatusAudit, RunsAudit) will be created in this schema.
    /// The schema is created automatically if it doesn't exist (when AutoApplyMigrations is true).
    /// </para>
    /// <para>
    /// If you change this value after initial migration, you must manually update the database schema
    /// or regenerate migrations with the new schema name.
    /// </para>
    /// </remarks>
    public string? SchemaName          { get; set; } = "EverTask";
}

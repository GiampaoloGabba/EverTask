namespace EverTask.Storage.MySql;

/// <summary>
/// Configuration options for MySQL/MariaDB task storage.
/// </summary>
public class MySqlTaskStoreOptions : ITaskStoreOptions
{
    /// <summary>
    /// Gets or sets whether to automatically apply pending migrations on startup. Default: true.
    /// </summary>
    /// <remarks>
    /// For production, consider setting this to false and applying migrations via deployment scripts.
    /// </remarks>
    public bool AutoApplyMigrations { get; set; } = true;

    /// <summary>
    /// Gets or sets the database schema name for EverTask tables. Default: "" (EMPTY).
    /// </summary>
    /// <remarks>
    /// MySQL and MariaDB have NO sub-database schema concept — a "schema" IS a database, selected by the
    /// connection string. Leave this empty: the tables live in the connection's database (mirrors SQLite,
    /// which also uses ""). Do not set a non-empty value; it would make EF emit cross-database references.
    /// </remarks>
    public string? SchemaName { get; set; } = "";

    /// <summary>
    /// Gets or sets an explicit server version. When null, the provider uses
    /// <c>ServerVersion.AutoDetect(connectionString)</c> (one extra connect at startup). Set an explicit value
    /// (e.g. <c>new MariaDbServerVersion(new Version(10, 11))</c> or <c>new MySqlServerVersion(new Version(8, 0))</c>)
    /// to skip auto-detection.
    /// </summary>
    public ServerVersion? ServerVersion { get; set; }
}

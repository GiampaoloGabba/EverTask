namespace EverTask.Storage.Postgres;

/// <summary>
/// Configuration options for PostgreSQL task storage.
/// </summary>
public class PostgresTaskStoreOptions : ITaskStoreOptions
{
    /// <summary>
    /// Gets or sets whether to automatically apply pending migrations on startup.
    /// Default: true.
    /// </summary>
    /// <remarks>
    /// When true, database schema updates are applied automatically when the application starts.
    /// For production, consider setting this to false and using deployment scripts to apply migrations.
    /// </remarks>
    public bool AutoApplyMigrations { get; set; } = true;

    /// <summary>
    /// Gets or sets the database schema name for EverTask tables. Default: "evertask" (LOWERCASE).
    /// </summary>
    /// <remarks>
    /// IMPORTANT: use a lowercase schema name. PostgreSQL folds UNQUOTED identifiers to lowercase, but
    /// EF/Npgsql ALWAYS double-quotes generated identifiers. A mixed-case "EverTask" becomes a permanently
    /// case-sensitive "EverTask" that every hand-written SQL/psql/search_path query must quote exactly.
    /// "evertask" stays quote-insensitive. Custom values should match ^[a-z_][a-z0-9_]*$.
    /// Schema changes are honored at runtime (the migrations are schema-aware, like SQL Server), but the
    /// generated migration snapshot is built against the design-time default — see the provider CLAUDE.md.
    /// </remarks>
    public string? SchemaName { get; set; } = "evertask";
}

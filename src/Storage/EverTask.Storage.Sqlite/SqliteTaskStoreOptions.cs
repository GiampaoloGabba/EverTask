namespace EverTask.Storage.Sqlite;

public class SqliteTaskStoreOptions : ITaskStoreOptions
{
    public bool    AutoApplyMigrations { get; set; } = true;
    public string? SchemaName          { get; set; } = "";
}

namespace EverTask.Storage.SqlServer;

public class SqlServerTaskStoreOptions : ITaskStoreOptions
{
    public bool    AutoApplyMigrations { get; set; } = true;
    public string? SchemaName          { get; set; } = "EverTask";
}

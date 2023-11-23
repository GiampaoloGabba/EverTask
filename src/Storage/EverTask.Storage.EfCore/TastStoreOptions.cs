namespace EverTask.Storage.EfCore;

public class TaskStoreOptions
{
    public bool    AutoApplyMigrations { get; set; } = true;
    public string? SchemaName          { get; set; } = "EverTask";
}

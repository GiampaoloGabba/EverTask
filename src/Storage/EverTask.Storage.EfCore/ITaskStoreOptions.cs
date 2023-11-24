namespace EverTask.Storage.EfCore;

public interface ITaskStoreOptions
{
    public bool    AutoApplyMigrations { get; set; }
    public string? SchemaName          { get; set; }
}

namespace EverTask.Storage.EfCore;

public interface ITaskStoreDbContext : IDisposable, IAsyncDisposable
{
    public string? Schema { get; }

    public DbSet<QueuedTask>  QueuedTasks { get; }
    public DbSet<StatusAudit> StatusAudit { get; }
    public DbSet<RunsAudit>   RunsAudit   { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

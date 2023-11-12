using EverTask.Storage;

namespace EverTask.EfCore;

public interface ITaskStoreDbContext : IDisposable, IAsyncDisposable
{
    public string? Schema { get; }

    public DbSet<QueuedTask>  QueuedTasks           { get; }
    public DbSet<StatusAudit> QueuedTaskStatusAudit { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

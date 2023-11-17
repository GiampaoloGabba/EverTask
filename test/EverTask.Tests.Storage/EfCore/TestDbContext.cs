using EverTask.EfCore;
using EverTask.Storage;
using Microsoft.EntityFrameworkCore;

namespace EverTask.Tests.Storage.EfCore;

public class TestDbContext : DbContext, ITaskStoreDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options)
        : base(options)
    {
    }

    public         string?            Schema                { get; }      = "EverTask";
    public virtual DbSet<QueuedTask>  QueuedTasks           { get; set; } = null!;
    public virtual DbSet<StatusAudit> QueuedTaskStatusAudit { get; set; } = null!;
}

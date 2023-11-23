using EverTask.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EverTask.Storage.SqlServer;

public class TaskStoreEfDbContext(
    DbContextOptions<TaskStoreEfDbContext> options,
    IOptions<TaskStoreOptions> storeOptions)
    : DbContext(options), ITaskStoreDbContext
{
    public string? Schema { get; } = storeOptions.Value.SchemaName;

    public DbSet<QueuedTask>  QueuedTasks           => Set<QueuedTask>();
    public DbSet<StatusAudit> QueuedTaskStatusAudit => Set<StatusAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (!string.IsNullOrEmpty(Schema))
            modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Type)
                    .HasMaxLength(500)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Request)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Handler)
                    .HasMaxLength(500)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .Property(e => e.Status)
                    .HasConversion<string>().HasMaxLength(15)
                    .IsRequired();

        modelBuilder.Entity<QueuedTask>()
                    .HasIndex(q => q.Status)
                    .IsUnique(false);

        modelBuilder.Entity<QueuedTask>()
                    .HasMany(a => a.StatusAudits)
                    .WithOne(af => af.QueuedTask)
                    .HasForeignKey(af => af.QueuedTaskId)
                    .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<QueuedTask>()
                    .HasMany(a => a.RunsAudits)
                    .WithOne(af => af.QueuedTask)
                    .HasForeignKey(af => af.QueuedTaskId)
                    .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StatusAudit>()
                    .HasIndex(q => q.QueuedTaskId)
                    .IsUnique(false);

        modelBuilder.Entity<StatusAudit>()
                    .Property(e => e.NewStatus)
                    .HasConversion<string>().HasMaxLength(15)
                    .IsRequired();

        modelBuilder.Entity<RunsAudit>()
                    .HasIndex(q => q.QueuedTaskId)
                    .IsUnique(false);

        modelBuilder.Entity<RunsAudit>()
                    .Property(e => e.Status)
                    .HasConversion<string>().HasMaxLength(15)
                    .IsRequired();
    }
}

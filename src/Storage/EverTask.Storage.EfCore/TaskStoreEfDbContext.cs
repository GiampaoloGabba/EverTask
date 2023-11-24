using Microsoft.Extensions.Options;

namespace EverTask.Storage.EfCore;

public abstract class TaskStoreEfDbContext<T>(
    DbContextOptions<T> options,
    IOptions<ITaskStoreOptions> storeOptions)
    : DbContext(options), ITaskStoreDbContext where T : DbContext
{
    public string? Schema { get; } = storeOptions.Value.SchemaName;

    public DbSet<QueuedTask>  QueuedTasks => Set<QueuedTask>();
    public DbSet<StatusAudit> StatusAudit => Set<StatusAudit>();
    public DbSet<RunsAudit>   RunsAudit   => Set<RunsAudit>();

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
